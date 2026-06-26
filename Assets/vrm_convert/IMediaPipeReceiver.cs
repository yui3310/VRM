using System;

namespace CCMediaPipe
{
    /// <summary>
    /// Abstraction for receiving MediaPipe Holistic landmarks.
    /// Swap implementations (UDP, TCP, named pipe, native plugin)
    /// without touching the mapping logic.
    /// </summary>
    public interface IMediaPipeReceiver : IDisposable
    {
        /// <summary>True once the underlying connection is ready.</summary>
        bool IsConnected { get; }

        /// <summary>Start listening / connecting.</summary>
        void Start();

        /// <summary>Stop listening and release resources.</summary>
        void Stop();

        /// <summary>
        /// Try to get the latest frame (non-blocking).
        /// Returns null when no new frame is available.
        /// </summary>
        HolisticFrame TryGetLatestFrame();
    }
}
