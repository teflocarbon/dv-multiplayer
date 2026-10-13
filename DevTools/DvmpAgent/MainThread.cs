using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Dvmp.Agent;

/// <summary>
/// Marshals work from the HTTP threads onto Unity's main thread, including coroutine-style
/// probes that span several frames.
/// </summary>
internal sealed class MainThread : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> queue = new();
    private static MainThread instance;

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

    /// <summary>
    /// Runs <paramref name="start"/> on the main thread; if it yields an enumerator, drives it
    /// as a coroutine and completes with <paramref name="finish"/> once it ends.
    /// </summary>
    public static Task<T> RunMaybeCoroutine<T>(Func<object> start, Func<T> finish, Func<bool> cancelled)
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(() =>
        {
            object first;
            try { first = start(); }
            catch (Exception e) { tcs.TrySetException(e); return; }

            if (first is IEnumerator routine)
                instance.StartCoroutine(Drive(routine, tcs, finish, cancelled));
            else
                Complete(tcs, finish);
        });
        return tcs.Task;
    }

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

    private static void Complete<T>(TaskCompletionSource<T> tcs, Func<T> finish)
    {
        try { tcs.TrySetResult(finish()); }
        catch (Exception e) { tcs.TrySetException(e); }
    }

    // Steps the probe's enumerator by hand so exceptions surface as results instead of
    // vanishing into Unity's coroutine scheduler. Nested enumerators (including
    // CustomYieldInstruction such as WaitUntil) are flattened; other yield values
    // (WaitForSeconds, WaitForEndOfFrame, null) are handed to Unity unchanged.
    private static IEnumerator Drive<T>(IEnumerator root, TaskCompletionSource<T> tcs, Func<T> finish, Func<bool> cancelled)
    {
        Stack<IEnumerator> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (cancelled()) yield break;

            IEnumerator top = stack.Peek();
            bool moved;
            object current = null;
            try
            {
                moved = top.MoveNext();
                if (moved) current = top.Current;
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
                yield break;
            }

            if (!moved) { stack.Pop(); continue; }
            if (current is IEnumerator nested) { stack.Push(nested); continue; }
            yield return current;
        }
        Complete(tcs, finish);
    }
}
