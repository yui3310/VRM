using UnityEditor;
using UnityEngine;

namespace CCMediaPipe
{
    [CustomEditor(typeof(VRMHolisticController))]
    public class VRMHolisticControllerEditor : Editor
    {
        private SerializedProperty mappingsProperty;

        private void OnEnable()
        {
            mappingsProperty = serializedObject.FindProperty("boneMappings");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawPropertiesExcluding(serializedObject, "boneMappings");

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Humanoid Bone Mapping", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Each row maps a tracking role to the model's Humanoid bone. " +
                "Use LastBone to disable a role when the VRM does not provide it.",
                MessageType.Info);

            if (mappingsProperty != null)
            {
                for (int i = 0; i < mappingsProperty.arraySize; i++)
                {
                    SerializedProperty row = mappingsProperty.GetArrayElementAtIndex(i);
                    SerializedProperty role = row.FindPropertyRelative("role");
                    SerializedProperty targetBone = row.FindPropertyRelative("target");

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PropertyField(role, GUIContent.none, GUILayout.Width(150f));
                    EditorGUILayout.LabelField("->", GUILayout.Width(22f));
                    EditorGUILayout.PropertyField(targetBone, GUIContent.none);
                    if (GUILayout.Button("-", GUILayout.Width(24f)))
                    {
                        mappingsProperty.DeleteArrayElementAtIndex(i);
                        break;
                    }
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Add Mapping"))
                {
                    int index = mappingsProperty.arraySize;
                    mappingsProperty.InsertArrayElementAtIndex(index);
                    SerializedProperty row = mappingsProperty.GetArrayElementAtIndex(index);
                    row.FindPropertyRelative("role").enumValueIndex = 0;
                    row.FindPropertyRelative("target").enumValueIndex = 0;
                }

                if (GUILayout.Button("Complete Defaults"))
                {
                    serializedObject.ApplyModifiedProperties();
                    ((VRMHolisticController)target).EnsureBoneMappings();
                    EditorUtility.SetDirty(target);
                    serializedObject.Update();
                }

                if (GUILayout.Button("Reset Defaults"))
                {
                    serializedObject.ApplyModifiedProperties();
                    ((VRMHolisticController)target).ResetBoneMappingsToDefault();
                    EditorUtility.SetDirty(target);
                    serializedObject.Update();
                }
                EditorGUILayout.EndHorizontal();

                if (GUILayout.Button("Apply Mapping While Playing"))
                {
                    serializedObject.ApplyModifiedProperties();
                    ((VRMHolisticController)target).ApplyBoneMappingsToMapper();
                    EditorUtility.SetDirty(target);
                    serializedObject.Update();
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
