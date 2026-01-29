using UnityEngine;

/// <summary>
/// Bridge script for Flutter ↔ Unity messaging. The Flutter app calls these methods
/// on the GameObject named "FlutterUnityBridge". Sends "scene_loaded" to Flutter when ready.
/// </summary>
public class FlutterUnityBridge : MonoBehaviour
{
    [SerializeField]
    [Tooltip("Optional. If null, a child cube is created for rotation.")]
    private Transform rotatableTarget;

    private float rotationSpeed;
    private bool sceneLoadedSent;

    private void Awake()
    {
        if (rotatableTarget == null)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "RotatingCube";
            cube.transform.SetParent(transform);
            cube.transform.localPosition = Vector3.zero;
            cube.transform.localScale = Vector3.one;
            rotatableTarget = cube.transform;
        }
    }

    private void Start()
    {
        SendSceneLoadedOnce();
    }

    private void Update()
    {
        if (rotatableTarget != null && rotationSpeed != 0f)
        {
            rotatableTarget.Rotate(Vector3.up, rotationSpeed * Time.deltaTime);
        }
    }

    /// <summary>
    /// Called by Flutter with a number string (e.g. "50") to set rotation speed.
    /// </summary>
    public void SetRotationSpeed(string message)
    {
        if (float.TryParse(message, out float speed))
        {
            rotationSpeed = speed;
            Debug.Log($"[FlutterUnityBridge] SetRotationSpeed: {speed}");
        }
        else
        {
            Debug.LogWarning($"[FlutterUnityBridge] SetRotationSpeed: could not parse '{message}'");
        }
    }

    /// <summary>
    /// Called by Flutter with arbitrary text. Log and/or react in the scene.
    /// </summary>
    public void OnMessageFromFlutter(string message)
    {
        Debug.Log($"[FlutterUnityBridge] OnMessageFromFlutter: {message}");
    }

    /// <summary>
    /// Called by Flutter with a JSON string. Parse and use as needed.
    /// </summary>
    public void OnJsonFromFlutter(string message)
    {
        Debug.Log($"[FlutterUnityBridge] OnJsonFromFlutter: {message}");
        // Optional: use JsonUtility.FromJson&lt;YourType&gt;(message) if you have a matching class
    }

    /// <summary>
    /// Call this from Unity when you want to notify Flutter (e.g. button click).
    /// </summary>
    public void SendToFlutterMessage(string message)
    {
        SendToFlutter.Send(message);
    }

    private void SendSceneLoadedOnce()
    {
        if (sceneLoadedSent) return;
        sceneLoadedSent = true;
        SendToFlutter.Send("scene_loaded");
    }
}
