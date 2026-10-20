using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;

namespace Dvmp.Agent;

/// <summary>
/// Marshals work from the HTTP threads onto Unity's main thread. Probe coroutines run on this
/// object too, through <see cref="ProbeRegistry"/>.
/// </summary>
internal sealed class MainThread : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> queue = new();
    private static MainThread instance;

    internal static MainThread Instance => instance;

    public static float Fps { get; private set; }
    public static int Frame { get; private set; }

    private void Awake() => instance = this;

    private void Update()
    {
        Frame = Time.frameCount;
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f)
            Fps = Fps <= 0f ? 1f / dt : Mathf.Lerp(Fps, 1f / dt, 0.05f);

        while (queue.TryDequeue(out Action action))
        {
            try { action(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        try { ProbeRegistry.Sweep(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    public static Task<T> Run<T>(Func<T> work)
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }

    /// <summary>Queues <paramref name="work"/> on the main thread without waiting for it.</summary>
    public static void Post(Action work) => queue.Enqueue(work);

    public static Task<T> RunCoroutine<T>(Func<TaskCompletionSource<T>, IEnumerator> routine)
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(() =>
        {
            try { instance.StartCoroutine(routine(tcs)); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }
}
