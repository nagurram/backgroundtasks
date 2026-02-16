using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RatelimitWorkflow
{
    /// <summary>
    /// Configuration for rate limiting behavior.
    /// </summary>
    public sealed class RateLimitConfiguration
    {
        /// <summary>
        /// Maximum number of tasks that can be executed within the specified period.
        /// </summary>
        public int MaxTasksPerPeriod { get; }

        /// <summary>
        /// The time period for rate limiting.
        /// </summary>
        public TimeSpan Period { get; }

        /// <summary>
        /// Creates a new rate limit configuration.
        /// </summary>
        /// <param name="maxTasksPerPeriod">Maximum tasks per period (must be positive).</param>
        /// <param name="period">The time period (must be positive).</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if parameters are invalid.</exception>
        public RateLimitConfiguration(int maxTasksPerPeriod, TimeSpan period)
        {
            if (maxTasksPerPeriod <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxTasksPerPeriod), "Must be greater than zero.");

            if (period <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(period), "Must be greater than zero.");

            MaxTasksPerPeriod = maxTasksPerPeriod;
            Period = period;
        }
    }
}
