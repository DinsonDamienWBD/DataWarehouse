namespace DataWarehouse.Hardening.Tests.CiCd;

/// <summary>
/// INTENTIONAL WEAK TEST -- verifies Stryker gate catches inadequate test coverage.
/// Stryker should report a surviving mutant for the boundary condition.
///
/// The function under test: <c>IsPositive(int n) => n > 0</c>
/// The test only covers the happy path: <c>Assert.True(IsPositive(5))</c>
///
/// Stryker will mutate <c>n > 0</c> to <c>n >= 0</c> and the test will still pass,
/// producing a surviving mutant. This drops the mutation score below the gate
/// threshold, causing the PR to be rejected.
///
/// A proper test suite would also include:
/// - <c>Assert.False(IsPositive(0))</c>   -- catches >= mutation
/// - <c>Assert.False(IsPositive(-5))</c>  -- catches sign flip
///
/// We intentionally omit these to prove the gate works.
///
/// Report: "Stage 4 - Gate Verification"
/// </summary>
public class StrykerGateVerificationTests
{
    /// <summary>
    /// The function under test. Returns true if n is strictly positive.
    /// Stryker will mutate the > operator to >=, ==, <, etc.
    /// </summary>
    public static bool IsPositive(int n) => n > 0;

    /// <summary>
    /// Happy-path test only. INTENTIONALLY does not test boundary condition n == 0.
    /// When Stryker mutates <c>n > 0</c> to <c>n >= 0</c>, this test still passes
    /// because IsPositive(5) is true for both > and >=.
    /// This surviving mutant drops the mutation score, triggering the Stryker gate.
    /// </summary>
    [Fact]
    public void IsPositive_ReturnsTrue_ForPositiveNumber()
    {
        Assert.True(IsPositive(5));
    }

    /// <summary>
    /// Negative case. This test passes for both <c>n > 0</c> and <c>n >= 0</c>
    /// because -5 is negative either way. It does NOT help kill the >= mutant.
    /// </summary>
    [Fact]
    public void IsPositive_ReturnsFalse_ForNegativeNumber()
    {
        Assert.False(IsPositive(-5));
    }

    // INTENTIONALLY MISSING:
    // [Fact]
    // public void IsPositive_ReturnsFalse_ForZero()
    // {
    //     Assert.False(IsPositive(0));  // This would kill the >= mutant
    // }

    /// <summary>
    /// Verifies the structure of the test: the function exists and returns expected types.
    /// This is a meta-test to confirm the gate verification infrastructure is correct.
    /// </summary>
    [Fact]
    public void IsPositive_HasCorrectSignature()
    {
        // Verify the method exists and is callable
        var method = typeof(StrykerGateVerificationTests).GetMethod(nameof(IsPositive));
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(int), parameters[0].ParameterType);
    }
}
