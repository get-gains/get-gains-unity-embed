using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared mapping from Flutter/ML Kit landmark space to Unity world offsets.
/// X/Y are normalized image coords; Z is raw depth — scaled per frame so depth extent matches XY figure extent.
/// </summary>
public static class PoseLandmarkMapping
{
    /// <summary>
    /// Mean Z of LEFT_HIP and RIGHT_HIP when both exist; otherwise 0.
    /// </summary>
    public static double ComputeZReference(Dictionary<string, LandmarkPoint> landmarks)
    {
        if (landmarks == null) return 0.0;
        if (landmarks.TryGetValue("LEFT_HIP", out LandmarkPoint lh) &&
            landmarks.TryGetValue("RIGHT_HIP", out LandmarkPoint rh))
            return (lh.Z + rh.Z) * 0.5;
        return 0.0;
    }

    /// <summary>
    /// Maps hip-relative raw Z into figure space using the same units as mapped X/Y:
    /// zMult = clamp(spanXY / max(spanZ, zSpanFloor), cap) where spanXY is max bbox side in world XY.
    /// Returns 0 when there is no usable XY span (flat depth).
    /// </summary>
    public static float ComputeDepthMultiplier(
        Dictionary<string, LandmarkPoint> landmarks,
        float xyScale,
        double zRef,
        float zSpanFloor,
        float zMultiplierCap)
    {
        if (landmarks == null || landmarks.Count == 0) return 0f;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minDz = double.MaxValue, maxDz = double.MinValue;

        foreach (var kvp in landmarks)
        {
            LandmarkPoint p = kvp.Value;
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
            double dz = p.Z - zRef;
            if (dz < minDz) minDz = dz;
            if (dz > maxDz) maxDz = dz;
        }

        double spanNormX = maxX - minX;
        double spanNormY = maxY - minY;
        float spanXY = (float)(System.Math.Max(spanNormX, spanNormY) * xyScale);
        float spanZ = (float)(maxDz - minDz);

        if (spanXY <= 0f) return 0f;

        float zMult = spanXY / Mathf.Max(spanZ, zSpanFloor);
        return Mathf.Min(zMult, zMultiplierCap);
    }

    /// <summary>
    /// Landmark to local figure space: X/Y scaled by <paramref name="xyScale"/>;
    /// depth is <c>(Z - zRef) * depthMultiplier</c> when invert is off.
    /// </summary>
    public static Vector3 ToWorldPosition(
        LandmarkPoint p,
        float xyScale,
        float depthMultiplier,
        bool invertDepth,
        double zRef,
        Vector3 centerOffset)
    {
        float x = (float)(p.X - 0.5) * xyScale;
        float y = (float)(1.0 - p.Y - 0.5) * xyScale;
        float z = (float)((p.Z - zRef) * depthMultiplier);
        if (invertDepth) z = -z;
        return centerOffset + new Vector3(x, y, z);
    }

    private static readonly string[] HeadClusterKeys =
    {
        "NOSE", "LEFT_EYE", "RIGHT_EYE", "LEFT_EAR", "RIGHT_EAR",
    };

    private static readonly string[] ArmInvertZKeys =
    {
        "LEFT_SHOULDER", "RIGHT_SHOULDER",
        "LEFT_ELBOW", "RIGHT_ELBOW",
        "LEFT_WRIST", "RIGHT_WRIST",
        "LEFT_INDEX", "LEFT_PINKY", "RIGHT_INDEX", "RIGHT_PINKY",
    };

    /// <summary>
    /// Pulls head landmarks toward MID_SHOULDER to reduce neck-overextension (MID_SHOULDER key or L/R shoulder average).
    /// </summary>
    public static void ApplyHeadClusterBlend(
        Dictionary<string, Vector3> worldPos,
        float headReachScale,
        float headDepthScale)
    {
        if (worldPos == null || worldPos.Count == 0) return;

        Vector3 mid;
        if (!worldPos.TryGetValue("MID_SHOULDER", out mid))
        {
            if (worldPos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) &&
                worldPos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
                mid = 0.5f * (ls + rs);
            else
                return;
        }

        for (int i = 0; i < HeadClusterKeys.Length; i++)
        {
            string key = HeadClusterKeys[i];
            if (!worldPos.TryGetValue(key, out Vector3 p)) continue;
            Vector3 delta = p - mid;
            delta *= headReachScale;
            delta.z *= headDepthScale;
            worldPos[key] = mid + delta;
        }
    }

    /// <summary>
    /// Re-expresses head cluster positions in a torso frame so the face looks "out"
    /// from the chest (straight ahead in shoulder space) instead of following noisy
    /// lateral nose/eye offsets toward one shoulder.
    /// Call after <see cref="ApplyHeadClusterBlend"/>.
    /// </summary>
    /// <param name="poseScale">Same XY scale as <see cref="ToWorldPosition"/> (stabilizes thresholds).</param>
    /// <param name="headForwardSmoothed">Persistent unit vector; smoothed each call for side-view stability.</param>
    /// <param name="headForwardSmoothAlpha">Slerp factor toward measured forward (0 = no smoothing).</param>
    public static void ApplyHeadStraightAheadNearShoulder(
        Dictionary<string, Vector3> worldPos,
        float poseScale,
        ref Vector3 headForwardSmoothed,
        float headForwardSmoothAlpha)
    {
        if (worldPos == null || worldPos.Count == 0) return;

        // Prefer a fresh L/R average (e.g. after ApplyInvertArmWorldZ); MID_SHOULDER may be stale.
        Vector3 mid;
        if (worldPos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) &&
            worldPos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
            mid = 0.5f * (ls + rs);
        else if (!worldPos.TryGetValue("MID_SHOULDER", out mid))
            return;

        if (!worldPos.TryGetValue("MID_HIP", out Vector3 midHip))
            return;

        Vector3 spineUp = mid - midHip;
        if (spineUp.sqrMagnitude < 1e-10f)
            return;
        spineUp.Normalize();

        float torsoLen = Vector3.Distance(midHip, mid);
        float minSpan = Mathf.Max(torsoLen * 0.09f, poseScale * 0.035f);
        float minForward = Mathf.Max(torsoLen * 0.07f, poseScale * 0.015f);

        if (!TryGetStableShoulderHorizontal(
                worldPos,
                spineUp,
                minSpan,
                out Vector3 shoulderHoriz,
                out float shoulderSpanRatio))
            return;

        Vector3 headForwardRaw = Vector3.Cross(shoulderHoriz, spineUp);
        if (headForwardRaw.sqrMagnitude < 1e-10f)
            return;
        headForwardRaw.Normalize();

        if (!worldPos.TryGetValue("NOSE", out Vector3 nose))
            return;

        Vector3 rawNoseDir = nose - mid;
        if (rawNoseDir.sqrMagnitude > 1e-10f && Vector3.Dot(headForwardRaw, rawNoseDir) < 0f)
            headForwardRaw = -headForwardRaw;

        float alpha = Mathf.Clamp01(headForwardSmoothAlpha);
        if (alpha <= 0f || headForwardSmoothed.sqrMagnitude < 1e-10f)
            headForwardSmoothed = headForwardRaw;
        else
        {
            if (Vector3.Dot(headForwardRaw, headForwardSmoothed) < 0f)
                headForwardRaw = -headForwardRaw;
            headForwardSmoothed = Vector3.Slerp(headForwardSmoothed, headForwardRaw, alpha).normalized;
        }

        Vector3 headForward = headForwardSmoothed;

        const float upWeight = 0.22f;
        const float noseLateralWeight = 0f;
        const float featureLateralWeight = 0.48f;
        // Side / X-axis views: shoulder line is nearly parallel to spine → spanRatio small → kill lateral bob.
        float lateralScale = shoulderSpanRatio * shoulderSpanRatio;

        for (int i = 0; i < HeadClusterKeys.Length; i++)
        {
            string key = HeadClusterKeys[i];
            if (!worldPos.TryGetValue(key, out Vector3 p))
                continue;

            Vector3 o = p - mid;
            float lateralW = (key == "NOSE" ? noseLateralWeight : featureLateralWeight) * lateralScale;
            float alongForward = Vector3.Dot(o, headForward);
            float minF = key == "NOSE" ? minForward : minForward * 0.78f;
            if (alongForward < minF)
                alongForward = minF;

            float alongUp = Vector3.Dot(o, spineUp) * upWeight;
            float alongShoulder = Vector3.Dot(o, shoulderHoriz) * lateralW;

            worldPos[key] = mid
                + headForward * alongForward
                + spineUp * alongUp
                + shoulderHoriz * alongShoulder;
        }
    }

    /// <summary>
    /// Left–right in the shoulder girdle, stable in side view: project shoulder line onto plane ⊥ spine,
    /// fall back to projected hip line, then a geometry-only axis so normalization does not chase depth noise.
    /// </summary>
    private static bool TryGetStableShoulderHorizontal(
        Dictionary<string, Vector3> worldPos,
        Vector3 spineUpUnit,
        float minSpan,
        out Vector3 shoulderHorizUnit,
        out float shoulderSpanRatio)
    {
        shoulderHorizUnit = default;
        shoulderSpanRatio = 0f;

        if (!worldPos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) ||
            !worldPos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
            return false;

        Vector3 shoulderProj = Vector3.ProjectOnPlane(rs - ls, spineUpUnit);
        float shoulderMag = shoulderProj.magnitude;
        shoulderSpanRatio = Mathf.Clamp01(shoulderMag / Mathf.Max(minSpan, 1e-4f));

        Vector3 primary = shoulderProj;
        if (shoulderMag < minSpan * 0.55f &&
            worldPos.TryGetValue("LEFT_HIP", out Vector3 lh) &&
            worldPos.TryGetValue("RIGHT_HIP", out Vector3 rh))
        {
            Vector3 hipProj = Vector3.ProjectOnPlane(rh - lh, spineUpUnit);
            if (hipProj.magnitude >= shoulderMag * 1.05f || shoulderMag < minSpan * 0.25f)
                primary = hipProj;
        }

        if (primary.sqrMagnitude < 1e-10f)
        {
            // Profile / camera along body X: build a stable horizontal without using noisy shoulder depth.
            Vector3 refDir = Mathf.Abs(Vector3.Dot(spineUpUnit, Vector3.up)) > 0.88f
                ? Vector3.forward
                : Vector3.up;
            primary = Vector3.Cross(spineUpUnit, refDir);
            if (primary.sqrMagnitude < 1e-10f)
                primary = Vector3.Cross(refDir, spineUpUnit);
        }

        shoulderHorizUnit = primary.normalized;
        return true;
    }

    /// <summary>
    /// Debug: negate world Z for arm-chain landmarks (after depth mapping).
    /// </summary>
    public static void ApplyInvertArmWorldZ(Dictionary<string, Vector3> worldPos, bool enabled)
    {
        if (!enabled || worldPos == null) return;
        for (int i = 0; i < ArmInvertZKeys.Length; i++)
        {
            string key = ArmInvertZKeys[i];
            if (!worldPos.TryGetValue(key, out Vector3 p)) continue;
            worldPos[key] = new Vector3(p.x, p.y, -p.z);
        }
    }

    /// <summary>
    /// Debug: negate world Z on head cluster (nose/eyes/ears). Call after head straightening if used.
    /// </summary>
    public static void ApplyInvertHeadWorldZ(Dictionary<string, Vector3> worldPos, bool enabled)
    {
        if (!enabled || worldPos == null) return;
        for (int i = 0; i < HeadClusterKeys.Length; i++)
        {
            string key = HeadClusterKeys[i];
            if (!worldPos.TryGetValue(key, out Vector3 p)) continue;
            worldPos[key] = new Vector3(p.x, p.y, -p.z);
        }
    }
}
