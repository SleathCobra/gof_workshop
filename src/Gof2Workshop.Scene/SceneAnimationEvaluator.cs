using System.Numerics;

namespace Gof2Workshop.Scene;

public static class SceneAnimationEvaluator
{
    public static SceneTransform Evaluate(
        SceneAnimationClip clip,
        int primitiveIndex,
        float timeSeconds,
        bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(clip);
        SceneAnimationTrack? track = clip.Tracks.FirstOrDefault(
            candidate => candidate.PrimitiveIndex == primitiveIndex);
        if (track is null || track.Keys.Count == 0)
        {
            return SceneTransform.Identity;
        }

        float time = NormalizeTime(timeSeconds, clip.DurationSeconds, loop);
        if (time <= track.Keys[0].TimeSeconds)
        {
            return ToTransform(track.Keys[0]);
        }

        if (time >= track.Keys[^1].TimeSeconds)
        {
            return ToTransform(track.Keys[^1]);
        }

        for (int index = 1; index < track.Keys.Count; index++)
        {
            SceneTransformKey next = track.Keys[index];
            if (time > next.TimeSeconds)
            {
                continue;
            }

            SceneTransformKey previous = track.Keys[index - 1];
            float span = next.TimeSeconds - previous.TimeSeconds;
            float amount = span <= 1e-7f ? 0 : (time - previous.TimeSeconds) / span;
            return new SceneTransform(
                Vector3.Lerp(previous.Translation, next.Translation, amount),
                Quaternion.Normalize(Quaternion.Slerp(previous.Rotation, next.Rotation, amount)),
                Vector3.Lerp(previous.Scale, next.Scale, amount));
        }

        return ToTransform(track.Keys[^1]);
    }

    private static float NormalizeTime(float time, float duration, bool loop)
    {
        if (!float.IsFinite(time) || time <= 0 || duration <= 0)
        {
            return 0;
        }

        return loop ? time % duration : Math.Min(time, duration);
    }

    private static SceneTransform ToTransform(SceneTransformKey key)
    {
        return new SceneTransform(key.Translation, key.Rotation, key.Scale);
    }
}
