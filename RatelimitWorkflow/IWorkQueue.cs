using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RatelimitWorkflow
{
    /// <summary>
    /// Defines a work queue that processes tasks with rate limiting and supports graceful shutdown.
    /// </summary>
    public interface IWorkQueue : IAsyncDisposable
    {
        /// <summary>
        /// Enqueues an asynchronous operation to be executed by the work queue.
        /// </summary>
        /// <typeparam name="TResult">The type of result returned by the operation.</typeparam>
        /// <param name="operation">The async operation to execute. Receives a cancellation token that will be triggered during shutdown.</param>
        /// <param name="cancellationToken">Optional token to cancel the operation before it starts executing.</param>
        /// <returns>A task that completes with the operation result, or is cancelled if the token is triggered before execution starts.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the queue has been disposed.</exception>
        Task<TResult> EnqueueAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default);
    }
}