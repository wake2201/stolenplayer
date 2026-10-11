using System;
using System.Collections;
using System.Collections.Generic;

namespace ThiefSim2Multiplayer.Core
{
    // Unity does not reliably dispose failed nested iterator coroutines. Own the iterator stack
    // so native fixtures always run their finally blocks before reporting a failure.
    public static class GuardedRoutine
    {
        public static IEnumerator Run(IEnumerator root, Action<Exception> failed)
        {
            var stack=new Stack<IEnumerator>(); stack.Push(root);
            try {
                while(stack.Count>0) {
                    object value=null; bool moved=false; Exception error=null;
                    try { moved=stack.Peek().MoveNext(); if(moved) value=stack.Peek().Current; }
                    catch(Exception e) { error=e; }
                    if(error!=null) { DisposeAll(stack); failed(error); yield break; }
                    if(!moved) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                    if(value is IEnumerator child) { stack.Push(child); continue; }
                    yield return value;
                }
            } finally { DisposeAll(stack); }
        }
        private static void DisposeAll(Stack<IEnumerator> stack)
        {
            Exception error=null;
            while(stack.Count>0) try { (stack.Pop() as IDisposable)?.Dispose(); } catch(Exception e) { error=e; }
            if(error!=null) throw new InvalidOperationException("Routine cleanup failed.",error);
        }
    }
}
