﻿using System;
using System.Runtime.CompilerServices;
using System.Threading;
using GigGossipSettler;
using Nito.AsyncEx;

#pragma warning disable 1591

namespace GigGossipSettlerAPI;
public class AsyncComQueue<T>
{
    AsyncMonitor asyncRevealMonitor;
    Queue<T> revealQueue = new();
    public AsyncComQueue()
    {
        asyncRevealMonitor = new AsyncMonitor(); 
	}

    public void Enqueue(T e)
    {
        using (asyncRevealMonitor.Enter())
        {
            revealQueue.Enqueue(e);
            asyncRevealMonitor.PulseAll();
        }
    }

    public async IAsyncEnumerable<T> DequeueAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            using (await asyncRevealMonitor.EnterAsync(cancellationToken))
            {
                await asyncRevealMonitor.WaitAsync(cancellationToken);
                while (revealQueue.Count > 0)
                {
                    var ic = revealQueue.Dequeue();
                    yield return ic;
                }
            }
        }
    }
}

