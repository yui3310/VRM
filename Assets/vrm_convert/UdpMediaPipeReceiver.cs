using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>
    /// Receives Holistic landmark JSON frames over UDP.
    /// Runs a background listener thread; TryGetLatestFrame() is
    /// called from Unity's main thread each Update.
    ///
    /// Expected JSON schema (one UDP packet per frame):
    /// {
    ///   "ts": 123456789,
    ///   "pw": [[x,y,z,v], ...33],   // pose world landmarks
    ///   "p":  [[x,y,z,v], ...33],   // pose normalized landmarks
    ///   "f":  [[x,y,z,v], ...468],  // face landmarks
    ///   "lhw": [[x,y,z,v], ...21],  // left hand world
    ///   "rhw": [[x,y,z,v], ...21],  // right hand world
    ///   "lh":  [[x,y,z,v], ...21],  // left hand normalized
    ///   "rh":  [[x,y,z,v], ...21]   // right hand normalized
    /// }
    /// All landmark groups are optional; only "pw" is required for
    /// body mapping.
    /// </summary>
    public class UdpMediaPipeReceiver : IMediaPipeReceiver
    {
        private readonly int port;
        private UdpClient udpClient;
        private Thread listenerThread;
        private volatile bool running;
        private HolisticFrame latestFrame;
        private readonly object frameLock = new object();

        public bool IsConnected => running && udpClient?.Client?.IsBound == true;

        public UdpMediaPipeReceiver(int port = 9999)
        {
            this.port = port;
        }

        public void Start()
        {
            if (running) return;

            try
            {
                udpClient = new UdpClient(port);
                udpClient.Client.ReceiveBufferSize = 1 << 20; // 1 MB
                running = true;
                listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "UdpMediaPipeReceiver",
                };
                listenerThread.Start();
                Debug.Log($"[UdpMediaPipeReceiver] Listening on port {port}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UdpMediaPipeReceiver] Failed to bind port {port}: {ex.Message}");
                Stop();
            }
        }

        public void Stop()
        {
            running = false;
            try { udpClient?.Close(); } catch { }
            udpClient = null;
            listenerThread = null;
        }

        public HolisticFrame TryGetLatestFrame()
        {
            lock (frameLock)
            {
                if (latestFrame == null) return null;
                var frame = latestFrame;
                latestFrame = null;
                return frame;
            }
        }

        public void Dispose() => Stop();

        private void ListenLoop()
        {
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    if (udpClient.Available > 0)
                    {
                        byte[] data = udpClient.Receive(ref endpoint);
                        var frame = ParseFrame(data);
                        if (frame != null)
                        {
                            lock (frameLock) { latestFrame = frame; }
                        }
                    }
                    else
                    {
                        Thread.Sleep(2);
                    }
                }
                catch (SocketException)
                {
                    if (running) Thread.Sleep(50);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UdpMediaPipeReceiver] Listener error: {ex.Message}");
                }
            }
        }

        private static HolisticFrame ParseFrame(byte[] data)
        {
            try
            {
                string json = Encoding.UTF8.GetString(data).Trim();
                if (json.Length < 2) return null;

                var frame = new HolisticFrame();
                // Lightweight JSON parsing without a full library dependency.
                // We use a simple tokenizer sufficient for the expected schema.
                int i = 1; // skip opening '{'

                while (i < json.Length)
                {
                    SkipWhitespace(json, ref i);
                    if (i >= json.Length || json[i] == '}') break;

                    string key = ReadString(json, ref i);
                    SkipWhitespace(json, ref i);
                    i++; // skip ':'
                    SkipWhitespace(json, ref i);

                    switch (key)
                    {
                        case "ts":
                            frame.timestamp = ReadLong(json, ref i);
                            break;
                        case "pw":
                            frame.poseWorldLandmarks = ReadLandmarkArray(json, ref i, HolisticFrame.PoseLandmarkCount);
                            break;
                        case "p":
                            frame.poseLandmarks = ReadLandmarkArray(json, ref i, HolisticFrame.PoseLandmarkCount);
                            break;
                        case "f":
                            frame.faceLandmarks = ReadLandmarkArray(json, ref i, HolisticFrame.FaceLandmarkCount);
                            break;
                        case "lhw":
                            frame.leftHandWorldLandmarks = ReadLandmarkArray(json, ref i, HolisticFrame.HandLandmarkCount);
                            break;
                        case "rhw":
                            frame.rightHandWorldLandmarks = ReadLandmarkArray(json, ref i, HolisticFrame.HandLandmarkCount);
                            break;
                        case "lh":
                            frame.leftHandLandmarks = ReadLandmarkArray(json, ref i, HolisticFrame.HandLandmarkCount);
                            break;
                        case "rh":
                            frame.rightHandLandmarks = ReadLandmarkArray(json, ref i, HolisticFrame.HandLandmarkCount);
                            break;
                        default:
                            SkipValue(json, ref i);
                            break;
                    }

                    SkipWhitespace(json, ref i);
                    if (i < json.Length && json[i] == ',') i++;
                }

                return frame;
            }
            catch
            {
                return null;
            }
        }

        // ---- Lightweight JSON helpers ----

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        private static string ReadString(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != '"') { i++; return ""; }
            i++; // skip opening quote
            int start = i;
            while (i < s.Length && s[i] != '"') i++;
            string result = s.Substring(start, i - start);
            i++; // skip closing quote
            return result;
        }

        private static long ReadLong(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-')) i++;
            if (long.TryParse(s.Substring(start, i - start), out long val)) return val;
            return 0;
        }

        private static float ReadFloat(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+'))
                i++;
            if (float.TryParse(
                s.Substring(start, i - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float val))
                return val;
            return 0f;
        }

        private static Landmark[] ReadLandmarkArray(string s, ref int i, int expectedCount)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != '[') return null;
            i++; // skip '['

            var list = new Landmark[expectedCount];
            int idx = 0;

            while (i < s.Length && idx < expectedCount)
            {
                SkipWhitespace(s, ref i);
                if (s[i] == ']') { i++; break; }

                // Expect inner array [x, y, z, v]
                SkipWhitespace(s, ref i);
                if (s[i] == '[')
                {
                    i++; // skip '['
                    list[idx].x = ReadFloat(s, ref i);
                    SkipWhitespace(s, ref i);
                    if (i < s.Length && s[i] == ',') i++;
                    list[idx].y = ReadFloat(s, ref i);
                    SkipWhitespace(s, ref i);
                    if (i < s.Length && s[i] == ',') i++;
                    list[idx].z = ReadFloat(s, ref i);
                    SkipWhitespace(s, ref i);
                    if (i < s.Length && s[i] == ',') i++;
                    list[idx].visibility = ReadFloat(s, ref i);
                    SkipWhitespace(s, ref i);
                    if (i < s.Length && s[i] == ']') i++;
                    idx++;
                }
                else
                {
                    SkipValue(s, ref i);
                }

                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',') i++;
            }

            // Skip to end of outer array
            int depth = 1;
            while (i < s.Length && depth > 0)
            {
                if (s[i] == '[') depth++;
                else if (s[i] == ']') depth--;
                i++;
            }

            return idx > 0 ? list : null;
        }

        private static void SkipValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) return;

            char c = s[i];
            if (c == '"') { ReadString(s, ref i); return; }
            if (c == '[')
            {
                int depth = 1; i++;
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '[') depth++;
                    else if (s[i] == ']') depth--;
                    i++;
                }
                return;
            }
            if (c == '{')
            {
                int depth = 1; i++;
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '{') depth++;
                    else if (s[i] == '}') depth--;
                    i++;
                }
                return;
            }
            // Number or literal
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && s[i] != ' ' && s[i] != '\t' && s[i] != '\n' && s[i] != '\r')
                i++;
        }
    }
}
