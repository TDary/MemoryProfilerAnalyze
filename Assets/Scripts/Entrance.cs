using UnityEditor;
#if UNITY_EDITOR
#endif

public class Entrance
{
    public static void StartServer()
    {
#if UNITY_EDITOR
        PlayerSettings.allowUnsafeCode = true;
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        UnityEditor.EditorApplication.EnterPlaymode();  //Play执行
#endif
    }
}