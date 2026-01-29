using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SendToFlutter
{
    public static void Send(string data) {
#if UNITY_EDITOR
        Debug.Log("SendToFlutter - " + data);
#elif UNITY_ANDROID
        // Use reflection to call the relevant static Kotlin method in the Android plugin
        using (AndroidJavaClass sendToFlutterClass = new AndroidJavaClass("com.learntoflutter.flutter_embed_unity_android.messaging.SendToFlutter"))
        {
            sendToFlutterClass.CallStatic("sendToFlutter", data);
        }
#endif
    }
}
