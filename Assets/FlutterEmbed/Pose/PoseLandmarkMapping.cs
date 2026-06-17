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
        Vector3 centerOffset,
        bool invertLandmarkX = false,
        bool invertLandmarkZ = false)
    {
        float x = (float)(p.X - 0.5) * xyScale;
        if (invertLandmarkX) x = -x;
        float y = (float)(1.0 - p.Y - 0.5) * xyScale;
        float z = (float)((p.Z - zRef) * depthMultiplier);
        if (invertDepth) z = -z;
        if (invertLandmarkZ) z = -z;
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

    /// <summary>Hip joints excluded so torso / Uniform-Z stay consistent; flips leg chain in world Z only.</summary>
    private static readonly string[] LegInvertZKeys =
    {
        "LEFT_KNEE", "RIGHT_KNEE",
        "LEFT_ANKLE", "RIGHT_ANKLE",
        "LEFT_HEEL", "RIGHT_HEEL",
        "LEFT_FOOT_INDEX", "RIGHT_FOOT_INDEX",
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
    /// Computes a stable body forward vector from torso landmarks.
    /// Returns false if no usable shoulder/hip frame.
    /// Forward points in the direction the chest/face is facing (toward the camera for a front view).
    /// </summary>
    /// <param name="worldPos">Mapped landmark positions in world/pose space.</param>
    /// <param name="forward">Output unit forward vector.</param>
    public static bool TryComputeBodyForward(IReadOnlyDictionary<string, Vector3> worldPos, out Vector3 forward)
    {
        forward = default;
        if (worldPos == null || worldPos.Count == 0) return false;

        if (!worldPos.TryGetValue("MID_SHOULDER", out Vector3 midShoulder))
        {
            if (worldPos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) &&
                worldPos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs))
                midShoulder = 0.5f * (ls + rs);
            else
                return false;
        }

        if (!worldPos.TryGetValue("MID_HIP", out Vector3 midHip))
        {
            if (worldPos.TryGetValue("LEFT_HIP", out Vector3 lh) &&
                worldPos.TryGetValue("RIGHT_HIP", out Vector3 rh))
                midHip = 0.5f * (lh + rh);
            else
                return false;
        }

        Vector3 spineUp = midShoulder - midHip;
        if (spineUp.sqrMagnitude < 1e-10f) return false;
        spineUp.Normalize();

        float torsoLen = Vector3.Distance(midHip, midShoulder);
        float minSpan = Mathf.Max(torsoLen * 0.09f, 0.01f);
        if (!TryGetStableShoulderHorizontal(worldPos, spineUp, minSpan, out Vector3 shoulderHoriz, out _))
            return false;

        forward = Vector3.Cross(shoulderHoriz, spineUp);
        if (forward.sqrMagnitude < 1e-10f) return false;
        forward.Normalize();

        // Nose should be in front of this forward; if not, flip.
        if (worldPos.TryGetValue("NOSE", out Vector3 nose))
        {
            Vector3 rawNoseDir = nose - midShoulder;
            if (rawNoseDir.sqrMagnitude > 1e-10f && Vector3.Dot(forward, rawNoseDir) < 0f)
                forward = -forward;
        }

        return true;
    }

    /// <summary>
    /// Left–right in the shoulder girdle, stable in side view: project shoulder line onto plane ⊥ spine,
    /// fall back to projected hip line, then a geometry-only axis so normalization does not chase depth noise.
    /// </summary>
    public static bool TryGetStableShoulderHorizontal(
        IReadOnlyDictionary<string, Vector3> worldPos,
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

    public static void ApplyInvertLegWorldZ(Dictionary<string, Vector3> worldPos, bool enabled)
    {
        if (!enabled || worldPos == null) return;
        for (int i = 0; i < LegInvertZKeys.Length; i++)
        {
            string key = LegInvertZKeys[i];
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

    // ── Torso / hip (3D vs 2D) debug ───────────────────────────────────────

    public enum TorsoDebugFlattenMode
    {
        None = 0,
        /// <summary>Same world Z for L/R shoulder and L/R hip (average). Reduces an X / bow-tie from depth.</summary>
        UniformZ = 1,
        /// <summary>Same world X for all four. Rare; try if L/R are inconsistent in X.</summary>
        UniformX = 2
    }

    public struct TorsoHipDebugInfo
    {
        public bool SidesCrossInXz;
        public float ZSpread;
        public float XSpread;
    }

    /// <summary>False if any corner missing. XZ = top-down. Crossing L-side vs R-side segments = twisted torso.</summary>
    public static bool TryComputeTorsoHipDebug(
        IReadOnlyDictionary<string, Vector3> pos, out TorsoHipDebugInfo info)
    {
        info = default;
        if (pos == null) return false;
        if (!pos.TryGetValue("LEFT_SHOULDER", out Vector3 ls) ||
            !pos.TryGetValue("RIGHT_SHOULDER", out Vector3 rs) ||
            !pos.TryGetValue("LEFT_HIP", out Vector3 lh) ||
            !pos.TryGetValue("RIGHT_HIP", out Vector3 rh))
            return false;

        Vector2 ls2 = new Vector2(ls.x, ls.z);
        Vector2 rs2 = new Vector2(rs.x, rs.z);
        Vector2 lh2 = new Vector2(lh.x, lh.z);
        Vector2 rh2 = new Vector2(rh.x, rh.z);

        float minZ = Mathf.Min(ls.z, rs.z, lh.z, rh.z);
        float maxZ = Mathf.Max(ls.z, rs.z, lh.z, rh.z);
        float minX = Mathf.Min(ls.x, rs.x, lh.x, rh.x);
        float maxX = Mathf.Max(ls.x, rs.x, lh.x, rh.x);

        info = new TorsoHipDebugInfo
        {
            SidesCrossInXz = SegmentsIntersectOpen2D(ls2, lh2, rs2, rh2),
            ZSpread = maxZ - minZ,
            XSpread = maxX - minX
        };
        return true;
    }

    public static void ApplyTorsoDebugFlatten(
        System.Collections.Generic.IDictionary<string, Vector3> pos,
        TorsoDebugFlattenMode mode)
    {
        if (mode == TorsoDebugFlattenMode.None || pos == null) return;
        if (!pos.TryGetValue("LEFT_SHOULDER", out var ls)) return;
        if (!pos.TryGetValue("RIGHT_SHOULDER", out var rs)) return;
        if (!pos.TryGetValue("LEFT_HIP", out var lh)) return;
        if (!pos.TryGetValue("RIGHT_HIP", out var rh)) return;

        if (mode == TorsoDebugFlattenMode.UniformZ)
        {
            float mz = 0.25f * (ls.z + rs.z + lh.z + rh.z);
            pos["LEFT_SHOULDER"] = new Vector3(ls.x, ls.y, mz);
            pos["RIGHT_SHOULDER"] = new Vector3(rs.x, rs.y, mz);
            pos["LEFT_HIP"] = new Vector3(lh.x, lh.y, mz);
            pos["RIGHT_HIP"] = new Vector3(rh.x, rh.y, mz);
        }
        else if (mode == TorsoDebugFlattenMode.UniformX)
        {
            float mx = 0.25f * (ls.x + rs.x + lh.x + rh.x);
            pos["LEFT_SHOULDER"] = new Vector3(mx, ls.y, ls.z);
            pos["RIGHT_SHOULDER"] = new Vector3(mx, rs.y, rs.z);
            pos["LEFT_HIP"] = new Vector3(mx, lh.y, lh.z);
            pos["RIGHT_HIP"] = new Vector3(mx, rh.y, rh.z);
        }
    }

    private static bool SegmentsIntersectOpen2D(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float o1 = Orient2D(a, b, c);
        float o2 = Orient2D(a, b, d);
        float o3 = Orient2D(c, d, a);
        float o4 = Orient2D(c, d, b);
        if (o1 * o2 >= 0f || o3 * o4 >= 0f) return false;
        return o1 * o2 < 0f && o3 * o4 < 0f;
    }

    private static float Orient2D(Vector2 a, Vector2 b, Vector2 c) =>
        (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
}
