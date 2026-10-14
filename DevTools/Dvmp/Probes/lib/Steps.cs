using System;
using System.Collections;
using System.Collections.Generic;

namespace Dvmp.Lib
{
    public static class Steps
    {
        /// <summary>
        /// Runs a coroutine step and reports its exception instead of aborting the caller.
        /// C# cannot `yield` inside a try with a catch, so the enumerator is stepped by hand
        /// (nested enumerators included). Usage:
        ///   string error = null;
        ///   yield return Steps.Try(Hands.Pickup(id), e => error = e.Message);
        /// </summary>
        public static IEnumerator Try(IEnumerator step, Action<Exception> onError)
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(step);
            while (stack.Count > 0)
            {
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
                    onError(e);
                    yield break;
                }
                if (!moved) { stack.Pop(); continue; }
                if (current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return current;
            }
        }
    }
}
