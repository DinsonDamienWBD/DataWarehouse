using System;
using System.Threading;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Authority
{
    /// <summary>
    /// Provides ambient authority context propagation through async call chains using <see cref="AsyncLocal{T}"/>.
    /// Allows downstream code to discover which authority decision is currently in effect without
    /// explicit parameter passing.
    /// <para>
    /// Usage pattern:
    /// <code>
    /// using (AuthorityContextPropagator.SetContext(quorumDecision))
    /// {
    ///     // All code here (including async continuations) sees quorumDecision as Current
    ///     await SomeOperationAsync();
    /// }
    /// // Previous context is restored here
    /// </code>
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Authority Chain (AUTH-09)")]
    public static class AuthorityContextPropagator
    {
        private static readonly AsyncLocal<AuthorityDecision?> _current = new();

        /// <summary>
        /// Gets the authority decision currently in effect for the ambient async context.
        /// Returns null if no authority context has been set.
        /// </summary>
        public static AuthorityDecision? Current => _current.Value;

        /// <summary>
        /// Sets the ambient authority context to the specified decision.
        /// Returns an <see cref="IDisposable"/> that restores the previous context when disposed.
        /// <para>
        /// This method is designed to be used in a <c>using</c> block to ensure proper cleanup
        /// of the authority context even if exceptions occur.
        /// </para>
        /// </summary>
        /// <param name="decision">The authority decision to set as the current context.</param>
        /// <returns>An <see cref="IDisposable"/> scope that restores the previous context on dispose.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="decision"/> is null.</exception>
        public static IDisposable SetContext(AuthorityDecision decision)
        {
            if (decision is null)
                throw new ArgumentNullException(nameof(decision));

            var previous = _current.Value;
            _current.Value = decision;
            return new AuthorityScope(previous);
        }

        /// <summary>
        /// Clears the ambient authority context, setting it to null.
        /// Typically used for cleanup in exceptional scenarios; prefer the scoped <see cref="SetContext"/>
        /// pattern for normal usage.
        /// </summary>
        public static void Clear()
        {
            _current.Value = null;
        }

        /// <summary>
        /// Disposable scope that captures and restores the previous authority context.
        /// </summary>
        private sealed class AuthorityScope : IDisposable
        {
            private readonly AuthorityDecision? _previous;
            private int _disposed;

            internal AuthorityScope(AuthorityDecision? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _current.Value = _previous;
                }
            }
        }
    }
}
