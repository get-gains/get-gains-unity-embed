using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Holds loaded pose frames and drives playback (current frame index, play/pause, seek).
/// Updates both the stick figure renderer AND the humanoid pose driver if present.
/// When a humanoid is connected, the stick figure is hidden automatically.
/// If useStickFigureOnly is true, the stick figure is always shown (reliable fallback).
/// Pose is applied in LateUpdate so bone rotations run after the Animator (otherwise a
/// RuntimeAnimatorController would overwrite script-driven humanoid bones every frame).
/// </summary>
[DefaultExecutionOrder(1000)]
public class PosePlaybackController : MonoBehaviour
{
    [SerializeField] private PoseStickFigureRenderer poseRenderer;
    [SerializeField] private HumanoidPoseDriver humanoidDriver;
    [Tooltip("When true, always show and drive the stick figure instead of the humanoid (reliable fallback).")]
    [SerializeField] private bool useStickFigureOnly = false;

    public void SetRenderer(PoseStickFigureRenderer renderer) { poseRenderer = renderer; }

    public void SetHumanoidDriver(HumanoidPoseDriver driver)
    {
        humanoidDriver = driver;
        if (humanoidDriver != null && poseRenderer != null && !useStickFigureOnly)
            poseRenderer.SetVisible(false);
    }

    public void SetUseStickFigureOnly(bool value)
    {
        useStickFigureOnly = value;
        if (poseRenderer != null && humanoidDriver != null)
        {
            if (useStickFigureOnly)
                poseRenderer.SetVisible(true);
            else
                poseRenderer.SetVisible(false);
        }
    }

    /// <summary>Bind the first active driveable HumanoidPoseDriver in the scene (e.g. after swapping character prefab).</summary>
    public void ResolveHumanoidDriver()
    {
        if (humanoidDriver != null && humanoidDriver.IsDriveable)
            return;
        humanoidDriver = HumanoidPoseDriver.FindBestDriveableDriver();
    }

    /// <summary>Currently resolved humanoid for orbit/camera; null if none driveable.</summary>
    public HumanoidPoseDriver ActiveHumanoidDriver => humanoidDriver;

    private List<PoseFrame> _frames = new List<PoseFrame>();
    private int _currentFrameIndex;
    private float _frameTime;
    private int _fps = 15;
    private bool _loop = true;
    private bool _playing;
    private Color _skeletonColor = Color.cyan;

    /// <summary>When true, keep the cyan stick figure visible on top of the humanoid for comparison.</summary>
    private bool _debugForceStickFigure;

    private void Awake()
    {
        if (poseRenderer == null)
            poseRenderer = GetComponentInChildren<PoseStickFigureRenderer>();
        if (humanoidDriver == null)
            humanoidDriver = GetComponentInChildren<HumanoidPoseDriver>();
        ResolveHumanoidDriver();
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
    }

    private void LateUpdate()
    {
        if (_frames == null || _frames.Count == 0) return;
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
    }

    public void SetSkeletonColor(Color color)
    {
        _skeletonColor = color;
        if (poseRenderer != null) poseRenderer.SetColor(color);
    }

    public void SetDebugForceStickFigure(bool value)
    {
        _debugForceStickFigure = value;
    }

    private void UpdateRenderer()
    {
        if (_frames == null || _frames.Count == 0) return;
        int idx = Mathf.Clamp(_currentFrameIndex, 0, _frames.Count - 1);
        var landmarks = _frames[idx].Landmarks;

        ResolveHumanoidDriver();
        if (humanoidDriver != null)
            humanoidDriver.TryInitialize();

        bool useHumanoid = humanoidDriver != null && humanoidDriver.IsDriveable && !useStickFigureOnly;

        if (poseRenderer != null)
        {
            if (useHumanoid && !_debugForceStickFigure)
                poseRenderer.SetVisible(false);
            else
            {
                poseRenderer.SetVisible(true);
                poseRenderer.UpdateFrame(landmarks);
            }
        }

        if (useHumanoid)
            humanoidDriver.ApplyPose(landmarks);
    }

    public int CurrentFrameIndex => _currentFrameIndex;
    public int FrameCount => _frames?.Count ?? 0;
}
