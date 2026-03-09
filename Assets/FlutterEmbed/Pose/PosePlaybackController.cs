using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Holds loaded pose frames and drives playback (current frame index, play/pause, seek).
/// Updates both the stick figure renderer AND the humanoid pose driver if present.
/// When a humanoid is connected, the stick figure is hidden automatically.
/// </summary>
public class PosePlaybackController : MonoBehaviour
{
    [SerializeField] private PoseStickFigureRenderer poseRenderer;
    [SerializeField] private HumanoidPoseDriver humanoidDriver;

    public void SetRenderer(PoseStickFigureRenderer renderer) { poseRenderer = renderer; }

    public void SetHumanoidDriver(HumanoidPoseDriver driver)
    {
        humanoidDriver = driver;
        if (humanoidDriver != null && poseRenderer != null)
            poseRenderer.SetVisible(false);
    }

    private List<PoseFrame> _frames = new List<PoseFrame>();
    private int _currentFrameIndex;
    private float _frameTime;
    private int _fps = 15;
    private bool _loop = true;
    private bool _playing;
    private Color _skeletonColor = Color.cyan;

    private void Awake()
    {
        if (poseRenderer == null)
            poseRenderer = GetComponentInChildren<PoseStickFigureRenderer>();
        if (humanoidDriver == null)
            humanoidDriver = GetComponentInChildren<HumanoidPoseDriver>();
    }

    private void Update()
    {
        if (!_playing || _frames == null || _frames.Count == 0) return;

        _frameTime += Time.deltaTime;
        float frameDuration = 1f / Mathf.Max(1, _fps);
        while (_frameTime >= frameDuration)
        {
            _frameTime -= frameDuration;
            _currentFrameIndex++;
            if (_currentFrameIndex >= _frames.Count)
            {
                if (_loop) _currentFrameIndex = 0;
                else
                {
                    _currentFrameIndex = _frames.Count - 1;
                    _playing = false;
                    return;
                }
            }
        }

        UpdateRenderer();
    }

    public void LoadFrames(List<PoseFrame> frames, int fps = 15, bool loop = true)
    {
        _frames = frames ?? new List<PoseFrame>();
        _fps = Mathf.Max(1, fps);
        _loop = loop;
        _currentFrameIndex = 0;
        _frameTime = 0f;
        _playing = false;
        UpdateRenderer();
    }

    public void Play()
    {
        if (_frames != null && _frames.Count > 0) _playing = true;
    }

    public void Pause()
    {
        _playing = false;
    }

    public void SeekToFrame(int index)
    {
        if (_frames == null) return;
        _currentFrameIndex = Mathf.Clamp(index, 0, _frames.Count - 1);
        _frameTime = 0f;
        UpdateRenderer();
    }

    public void SetSkeletonColor(Color color)
    {
        _skeletonColor = color;
        if (poseRenderer != null) poseRenderer.SetColor(color);
    }

    private void UpdateRenderer()
    {
        if (_frames == null || _frames.Count == 0) return;
        int idx = Mathf.Clamp(_currentFrameIndex, 0, _frames.Count - 1);
        var landmarks = _frames[idx].Landmarks;

        bool hasHumanoid = humanoidDriver != null;

        if (poseRenderer != null)
        {
            if (hasHumanoid)
                poseRenderer.SetVisible(false);
            else
                poseRenderer.UpdateFrame(landmarks);
        }

        if (hasHumanoid)
            humanoidDriver.ApplyPose(landmarks);
    }

    public int CurrentFrameIndex => _currentFrameIndex;
    public int FrameCount => _frames?.Count ?? 0;
}
