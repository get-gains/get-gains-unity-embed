using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Parses the LoadPoseFrames JSON from Flutter. Format:
/// { "frames": [ { "timestampMs": 0, "landmarks": { "LEFT_SHOULDER": { "x": 0.5, "y": 0.3, "z": 0.1, "confidence": 0.9 }, ... } }, ... ], "fps": 15, "loop": true }
/// </summary>
public static class PoseDataParser
{
    private static readonly Regex NumberRegex = new Regex(@"[-]?\d+\.?\d*", RegexOptions.Compiled);

    public static bool TryParse(string json, out PosePayload payload)
    {
        payload = null;
        if (string.IsNullOrEmpty(json)) return false;

        try
        {
            int fps = 15;
            bool loop = true;
            var frames = new List<PoseFrame>();

            // Extract "fps": 15
            var fpsMatch = Regex.Match(json, @"""fps""\s*:\s*(\d+)");
            if (fpsMatch.Success && int.TryParse(fpsMatch.Groups[1].Value, out int f)) fps = f;

            // Extract "loop": true/false
            var loopMatch = Regex.Match(json, @"""loop""\s*:\s*(true|false)");
            if (loopMatch.Success) loop = string.Equals(loopMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);

            // Find "frames": [ ... ]
            int framesStart = json.IndexOf("\"frames\"", StringComparison.Ordinal);
            if (framesStart < 0) return false;
            int arrayStart = json.IndexOf('[', framesStart);
            if (arrayStart < 0) return false;

            int depth = 1;
            int i = arrayStart + 1;
            int frameStart = -1;
            while (i < json.Length && depth > 0)
            {
                char c = json[i];
                if (c == '{')
                {
                    if (depth == 1) frameStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 1 && frameStart >= 0)
                    {
                        string frameJson = json.Substring(frameStart, i - frameStart + 1);
                        if (TryParseFrame(frameJson, out PoseFrame frame))
                            frames.Add(frame);
                        frameStart = -1;
                    }
                }
                else if (c == '[') depth++;
                else if (c == ']') depth--;
                i++;
            }

            payload = new PosePayload { Frames = frames, Fps = fps, Loop = loop };
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PoseDataParser] Parse error: {e.Message}");
            return false;
        }
    }

    private static bool TryParseFrame(string frameJson, out PoseFrame frame)
    {
        frame = new PoseFrame { Landmarks = new Dictionary<string, LandmarkPoint>() };

        var tsMatch = Regex.Match(frameJson, @"""timestampMs""\s*:\s*(\d+)");
        if (tsMatch.Success && long.TryParse(tsMatch.Groups[1].Value, out long ts))
            frame.TimestampMs = ts;

        int landmarksStart = frameJson.IndexOf("\"landmarks\"", StringComparison.Ordinal);
        if (landmarksStart < 0) return true;

        int objStart = frameJson.IndexOf('{', landmarksStart);
        if (objStart < 0) return true;

        // Parse each "KEY": { "x": n, "y": n, "z": n, "confidence": n } (only object values)
        int pos = objStart + 1;
        while (pos < frameJson.Length)
        {
            int keyStart = frameJson.IndexOf('"', pos);
            if (keyStart < 0) break;
            int keyEnd = frameJson.IndexOf('"', keyStart + 1);
            if (keyEnd < 0) break;
            string key = frameJson.Substring(keyStart + 1, keyEnd - keyStart - 1);

            int colon = frameJson.IndexOf(':', keyEnd);
            if (colon < 0) break;
            int valueStart = frameJson.IndexOf('{', colon);
            if (valueStart < 0) { pos = keyEnd + 1; continue; }
            int valueEnd = FindMatchingBrace(frameJson, valueStart);
            if (valueEnd < 0) break;

            string pointJson = frameJson.Substring(valueStart, valueEnd - valueStart + 1);
            if (TryParseLandmarkPoint(pointJson, out LandmarkPoint point))
                frame.Landmarks[key] = point;

            pos = valueEnd + 1;
        }

        return true;
    }

    private static int FindMatchingBrace(string s, int openIndex)
    {
        int depth = 1;
        for (int i = openIndex + 1; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static bool TryParseLandmarkPoint(string json, out LandmarkPoint point)
    {
        point = default;
        double x = 0, y = 0, z = 0, confidence = 0.5;
        var matches = NumberRegex.Matches(json);
        int idx = 0;
        foreach (Match m in matches)
        {
            if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
            if (idx == 0) x = v;
            else if (idx == 1) y = v;
            else if (idx == 2) z = v;
            else if (idx == 3) { confidence = v; break; }
            idx++;
        }
        point = new LandmarkPoint(x, y, z, confidence);
        return true;
    }
}

public class PosePayload
{
    public List<PoseFrame> Frames;
    public int Fps;
    public bool Loop;
}

public class PoseFrame
{
    public long TimestampMs;
    public Dictionary<string, LandmarkPoint> Landmarks;
}

public struct LandmarkPoint
{
    public double X, Y, Z, Confidence;
    public LandmarkPoint(double x, double y, double z, double confidence)
    {
        X = x; Y = y; Z = z; Confidence = confidence;
    }
}
