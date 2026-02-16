# Rate-Limited Work Queue Implementation

## Overview

This solution implements a production-quality, thread-safe work queue with rate limiting, graceful shutdown, and comprehensive cancellation support for .NET 6+.

## Project Structure

```
RateLimitedWorkQueue/
├── RateLimitedWorkQueue.Core/          # Core library
│   ├── IWorkQueue.cs                   # Public interface
│   ├── RateLimitedWorkQueue.cs         # Main implementation
│   └── RateLimitConfiguration.cs       # Configuration model
└── RateLimitedWorkQueue.Tests/         # Unit tests (xUnit)
    └── RateLimitedWorkQueueTests.cs    # Comprehensive test suite
```

## Usage Example

```csharp
var config = new RateLimitConfiguration(
    maxTasksPerPeriod: 10,
    period: TimeSpan.FromSeconds(1)
);

await using var queue = new RateLimitedWorkQueue(config);

var result = await queue.EnqueueAsync(async ct =>
{
    // Your async operation here
    await SomeApiCall(ct);
    return result;
});
```

## Assumptions

1. **Rate Limiting Precision**: The rate limit is enforced with best-effort precision. Due to scheduling and timing variations, there may be minor deviations (typically <100ms) in enforcement.

2. **Task Execution Order**: Tasks are executed in FIFO order, except when cancelled before execution starts.

3. **Resource Management**: The dispatcher uses a single background thread for queue processing. Individual operations execute on the thread pool.

4. **Cancellation Semantics**: 
   - Pre-execution cancellation removes the task from the queue
   - During-execution cancellation is propagated to the operation via the CancellationToken
   - Post-execution cancellation has no effect

5. **Exception Handling**: Exceptions from operations are captured and propagated through the returned Task. They do not affect the dispatcher or other queued items.

6. **Disposal Behavior**: DisposeAsync waits indefinitely for in-flight tasks to complete. In production, consider adding timeout logic.

7. **Rate Limit Window**: Uses a sliding window approach - tracks execution timestamps and removes expired ones dynamically.

---

## Design & Architecture Questions

### 1. Design Justification

#### Core Mechanism

The implementation uses the following key components:

**Queue Management**:
- `ConcurrentQueue<WorkItem>`: Thread-safe FIFO queue for pending operations
- `SemaphoreSlim _workAvailableSignal`: Efficient signaling mechanism for the dispatcher to wake up when work is available
- Abstract `WorkItem` base class with generic `WorkItem<TResult>` implementation to handle type safety while maintaining a single queue

**Rate Limiting**:
- `Queue<DateTime> _executionTimestamps`: Sliding window of execution times
- `object _rateLimitLock`: Protects the timestamp queue during rate limit checks
- Algorithm: Before each execution, remove timestamps older than the configured period, then check if capacity is available

**Cancellation & Shutdown**:
- `CancellationTokenSource _shutdownTokenSource`: Signals graceful shutdown to the dispatcher
- `CancellationTokenSource.CreateLinkedTokenSource()`: Combines user cancellation token with shutdown token
- `TaskCompletionSource<TResult>`: Bridges the async operation result back to the caller
- State machine in `WorkItem`: Tracks Pending → Executing → Cancelled transitions using `Interlocked` for thread safety

**Why This Approach?**

1. **ConcurrentQueue**: Lock-free for high-throughput enqueueing, perfect for multi-threaded scenarios
2. **SemaphoreSlim**: Minimal overhead for signaling, supports async waiting
3. **Single Dispatcher Thread**: Simplifies rate limiting logic - no race conditions on timestamp management
4. **Linked CancellationTokens**: Elegant way to respect both user and shutdown cancellation without complex coordination
5. **TaskCompletionSource**: Standard .NET pattern for bridging callback-style APIs to async/await
6. **State Machine**: Atomic state transitions prevent race conditions during cancellation

#### Thread Safety Strategy

- **Lock-free operations**: Enqueueing uses `ConcurrentQueue.Enqueue` (no locks)
- **Minimal locking**: Only the rate limit check uses a lock, which is held briefly
- **Atomic operations**: State transitions use `Interlocked.CompareExchange`
- **Immutable state**: Configuration is readonly after construction

---

### 2. Scalability & Evolution

#### Challenges with Current In-Memory Design in Distributed Environment

1. **No Shared State**: Each instance has its own queue and rate limit tracking
   - Rate limit is per-instance, not global
   - Cannot distribute work across instances
   - No visibility into other instances' queues

2. **Task Locality**: Operations enqueued to Instance A cannot be processed by Instance B
   - Load imbalance if traffic is uneven
   - No failover capability

3. **Memory Limits**: Queue grows unbounded in memory
   - Large backlogs can cause OOM
   - Lost on instance failure

4. **No Persistence**: Tasks evaporate if instance crashes before execution

5. **Coordination Complexity**: Achieving exact global rate limits requires distributed consensus

#### High-Level Architectural Approach

**Architecture: Distributed Work Queue with Redis**

```
┌─────────────┐     ┌─────────────┐     ┌─────────────┐
│  Service A  │     │  Service B  │     │  Service C  │
│  (Worker)   │────▶│  (Worker)   │◀────│  (Worker)   │
└──────┬──────┘     └──────┬──────┘     └──────┬──────┘
       │                   │                   │
       └───────────────────┼───────────────────┘
                           │
                    ┌──────▼──────┐
                    │    Redis    │
                    │  (Queue +   │
                    │  Rate Limit)│
                    └─────────────┘
```

**Components**:

1. **Redis as Distributed Queue**:
   - Use Redis Lists (`LPUSH`/`RPOP`) for FIFO queue
   - Atomic operations ensure no duplicate processing
   - Persistence options (RDB, AOF) for durability
   - Pub/Sub for work notifications

2. **Distributed Rate Limiting**:
   - **Option A - Sliding Window Log**: 
     - Redis Sorted Set with timestamps as scores
     - `ZADD` to add execution, `ZREMRANGEBYSCORE` to remove expired
     - `ZCARD` to count current window
     - Atomic with Lua script
   
   - **Option B - Token Bucket** (Preferred):
     - Use `redis-cell` module (provides `CL.THROTTLE` command)
     - Atomic token bucket implementation
     - Better performance at scale
     - Handles burst traffic gracefully

3. **Work Item Storage**:
   - Serialize operation metadata (not closures - use command pattern)
   - Store in Redis Hash: `{workitem:{id}}` → `{type, parameters, createdAt, ...}`
   - Queue contains only IDs, full data fetched on dequeue

4. **Failure Handling**:
   - **Visibility Timeout Pattern**: 
     - Use `BRPOPLPUSH` to atomically move from pending → processing list
     - Worker heartbeats or timeout returns item to queue
   - **Dead Letter Queue**: After N retries, move to DLQ for investigation

5. **Cancellation**:
   - Store cancellation tokens in Redis: `{cancellation:{id}}` → `{cancelled: bool}`
   - Check before execution
   - For running tasks, use external cancellation signal store

**Technologies**:

- **Message Queue**: Redis (for simplicity) or RabbitMQ/Azure Service Bus (for enterprise features)
- **Rate Limiting**: Redis with `redis-cell` module or custom Lua scripts
- **Serialization**: System.Text.Json for DTOs (command pattern instead of closures)
- **Observability**: OpenTelemetry for distributed tracing, Prometheus for metrics
- **Orchestration**: Kubernetes with horizontal pod autoscaling based on queue depth

**Alternative: Cloud-Native Approach**

For cloud environments, use managed services:
- **Azure**: Service Bus + Azure Functions with scaling rules
- **AWS**: SQS + Lambda with reserved concurrency for rate limiting
- **GCP**: Cloud Tasks with rate limiting configuration

**Migration Path**:

1. **Phase 1**: Introduce `IWorkQueue` abstraction (already done ✓)
2. **Phase 2**: Implement `RedisWorkQueue : IWorkQueue`
3. **Phase 3**: Use factory pattern to choose implementation based on configuration
4. **Phase 4**: Gradual rollout with feature flags

---

### 3. API and Extensibility - Priority Support

**Requirement**: Support high-priority tasks that jump to the front of the queue.

**Interface Changes**:

```csharp
public enum TaskPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

public interface IWorkQueue : IAsyncDisposable
{
    Task<TResult> EnqueueAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
    
    // New overload with priority
    Task<TResult> EnqueueAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        TaskPriority priority,
        CancellationToken cancellationToken = default);
}
```

**Implementation Changes**:

1. **Replace ConcurrentQueue with Priority-Aware Storage**:
   
   ```csharp
   // Instead of single queue:
   private readonly ConcurrentQueue<WorkItem> _queue;
   
   // Use priority buckets:
   private readonly ConcurrentDictionary<TaskPriority, ConcurrentQueue<WorkItem>> _priorityQueues;
   
   // Or use a proper priority queue (with thread safety):
   private readonly PriorityQueue<WorkItem, TaskPriority> _queue;
   private readonly object _queueLock = new();
   ```

2. **Dequeue Logic** (in dispatcher):
   
   ```csharp
   private WorkItem? TryDequeueHighestPriority()
   {
       // Option A: Iterate priorities high to low
       foreach (var priority in new[] { TaskPriority.Critical, TaskPriority.High, 
                                        TaskPriority.Normal, TaskPriority.Low })
       {
           if (_priorityQueues[priority].TryDequeue(out var item))
               return item;
       }
       return null;
       
       // Option B: Use PriorityQueue<T, TPriority> (.NET 6+)
       lock (_queueLock)
       {
           if (_queue.Count > 0)
               return _queue.Dequeue();
           return null;
       }
   }
   ```

3. **Work Available Signaling**: No changes needed - still use `SemaphoreSlim`

4. **Rate Limiting Consideration**: Priority affects execution order, but all tasks still count toward rate limit equally. Alternative: Different rate limits per priority tier.

**Additional Enhancement - Preemption** (Advanced):

For critical tasks, optionally allow preemption:
- Maintain count of currently executing tasks
- If critical task arrives and we're at limit, optionally cancel lowest-priority running task
- Requires tracking executing tasks and their priorities

**Backward Compatibility**:

The single-parameter overload can default to `TaskPriority.Normal`, maintaining existing behavior.

---

### 4. Testing Philosophy & Self-Critique

#### Most Difficult Scenario to Test

**Rate Limiting Under Concurrent Load**

**Why It's Difficult**:

1. **Timing Sensitivity**: Rate limiting depends on precise timing, but tests run on shared CI infrastructure with variable CPU scheduling
2. **Non-Determinism**: Thread scheduling is non-deterministic - can't guarantee exact execution order
3. **Race Conditions**: The exact moment tasks execute depends on dispatcher timing, rate limit checks, and thread pool availability
4. **Tolerance Tuning**: Tests need tolerance for timing variations, but too much tolerance makes tests meaningless

**My Approach**:

- Use timestamp collection to verify sliding window behavior
- Check invariants (no more than N tasks per period) rather than exact timing
- Add tolerance (100ms) for timing checks
- Use high task counts to detect systematic violations despite timing noise
- In production, I'd add integration tests with time mocking (e.g., `ISystemClock` abstraction)

#### Confidence in Tests

**Rate Limiting Tests**: **7/10 Confidence**
- ✅ Verify core invariant (max N per period)
- ✅ Test multiple scenarios (different configs, loads)
- ❌ Can't easily test exact edge cases (e.g., tasks completing exactly at period boundary)
- ❌ Difficult to prove no subtle race conditions without formal verification
- **Improvement**: Add property-based testing (FsCheck) to test with random timings

**Shutdown Logic Tests**: **9/10 Confidence**
- ✅ Test all three requirements (reject new, cancel queued, wait for in-flight)
- ✅ Test concurrent disposal
- ✅ Verify idempotency
- ❌ Don't test extreme cases (e.g., thousands of queued items during disposal)
- **Improvement**: Add stress tests with higher task counts

**Cancellation Tests**: **8/10 Confidence**
- ✅ Test pre-execution cancellation
- ✅ Test during-execution cancellation
- ✅ Test multiple simultaneous cancellations
- ❌ Edge case: Cancellation racing with execution start
- **Improvement**: Use synchronization primitives to test exact race condition

#### Limitations of Current Implementation

1. **Unbounded Queue**: No max queue size - can cause memory issues under sustained overload
   - **Fix**: Add `MaxQueueSize` configuration, throw or block when full

2. **No Metrics/Observability**: Can't monitor queue depth, execution rates, cancellations
   - **Fix**: Add `IWorkQueueMetrics` interface, expose counters

3. **Dispatcher Exception Handling**: Unexpected exceptions in dispatcher loop are swallowed
   - **Fix**: Add structured logging, expose events for monitoring

4. **No Timeout Support**: Operations can run forever
   - **Fix**: Add optional `timeout` parameter, cancel after expiry

5. **Memory Pressure**: Timestamp queue grows unbounded if rate is slower than period
   - **Fix**: Limit timestamp storage or use circular buffer

6. **No Backpressure**: Callers aren't slowed down when queue is deep
   - **Fix**: Make `EnqueueAsync` return `ValueTask` and potentially await backpressure signal

7. **Disposal Timeout**: `DisposeAsync` waits indefinitely for in-flight tasks
   - **Fix**: Add `ShutdownTimeout` configuration, force-cancel after timeout

8. **CPU Spinning**: Rate limit check uses `Task.Delay` which isn't perfectly precise
   - **Fix**: Use `PeriodicTimer` (.NET 6+) for more efficient waiting

#### If I Had Another Day

**Priority 1 - Production Readiness**:
- Add comprehensive logging (Serilog/Microsoft.Extensions.Logging)
- Add metrics collection (AppMetrics or OpenTelemetry)
- Add health check endpoint support
- Add configurable max queue size with overflow strategies (block, reject, drop oldest)

**Priority 2 - Enhanced Features**:
- Implement priority queue support (as described in question 3)
- Add operation timeout support
- Add retry policies with exponential backoff
- Add bulk enqueue API for efficiency

**Priority 3 - Reliability**:
- Add circuit breaker pattern for failing operations
- Add graceful degradation (rate limit bypass in critical scenarios)
- Add better shutdown timeout handling
- Performance profiling and optimization (reduce allocations)

**Priority 4 - Testing**:
- Add property-based tests (FsCheck)
- Add chaos engineering tests (random failures, delays)
- Add load tests with realistic scenarios
- Add mutation testing to verify test quality

---

## AI Usage Disclosure

I used Claude AI to assist with this implementation in the following ways:

1. **Architecture Review**: Discussed trade-offs between different queue implementations (ConcurrentQueue vs PriorityQueue)
2. **Code Generation**: Generated boilerplate for test scaffolding and XML documentation
3. **Best Practices**: Verified async/await patterns and CancellationToken usage align with .NET guidelines
4. **Test Case Ideation**: Brainstormed edge cases and race conditions to test

All core logic, design decisions, and algorithm implementations were created by me with understanding of the requirements and can be explained in detail during the live review.

---

## Building and Running Tests

```bash
# Restore dependencies
dotnet restore

# Build solution
dotnet build

# Run tests
dotnet test

# Run tests with coverage
dotnet test /p:CollectCoverage=true
```

---

## License

This is assessment code. All rights reserved.
