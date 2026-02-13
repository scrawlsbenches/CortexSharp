// ============================================================================
// CortexSharp — Lateral Voting Interface
// ============================================================================
// Lateral voting is the mechanism by which cortical columns reach consensus
// on what object is being sensed. It implements the "Thousand Brains" voting
// process: each column has its own L2/3 representation of the candidate
// object(s), and columns exchange these representations laterally to
// eliminate inconsistent candidates.
//
// Biological basis:
//   L2/3 pyramidal neurons have long-range horizontal connections to
//   L2/3 neurons in other columns within the same region. These connections
//   carry object representation SDRs. When two columns' representations
//   overlap, they mutually reinforce the shared bits (intersection).
//   When they disagree, the non-shared bits are weakened or eliminated.
//
// The voting process:
//   1. Each column computes its L2/3 representation independently
//   2. Columns broadcast their representations laterally
//   3. A consensus is computed (bits supported by a threshold of columns)
//   4. Consensus is fed back to each column, which intersects it with
//      its own representation (narrowing candidates)
//   5. Repeat until convergence (all columns agree) or max iterations
//
// Convergence:
//   After convergence, all columns have the same (or highly overlapping)
//   representation. This IS recognition — no separate library lookup
//   is needed. The converged representation IS the object identity.
//
// Reference: Hawkins et al. (2017), "A Theory of How Columns in the
//            Neocortex Enable Learning the Structure of the World"
// ============================================================================

using CortexSharp.Core;

namespace CortexSharp.Algorithms;

/// <summary>
/// Lateral voting mechanism for cross-column consensus.
/// </summary>
public interface ILateralVoting
{
    /// <summary>
    /// Compute consensus from column votes and check for convergence.
    /// </summary>
    /// <param name="columnVotes">
    /// L2/3 object representations from each column.
    /// Each SDR represents that column's current candidate set.
    /// </param>
    /// <returns>Voting result including consensus SDR and convergence status.</returns>
    VotingResult ComputeConsensus(SDR[] columnVotes);

    /// <summary>
    /// Run the full iterative voting loop: compute consensus, feed back
    /// to columns, repeat until convergence or max iterations.
    /// </summary>
    /// <param name="columnVotes">Initial column representations.</param>
    /// <param name="feedbackAction">
    /// Callback to feed consensus back to columns and get updated votes.
    /// Called once per iteration with the current consensus SDR.
    /// Returns the updated column votes for the next iteration.
    /// </param>
    /// <returns>Final voting result after convergence or max iterations.</returns>
    VotingResult RunVotingLoop(
        SDR[] columnVotes,
        Func<SDR, SDR[]> feedbackAction);
}

/// <summary>
/// Result of a lateral voting computation.
/// </summary>
public record VotingResult
{
    /// <summary>
    /// Consensus SDR — bits supported by enough columns.
    /// After convergence, this IS the recognized object representation.
    /// </summary>
    public required SDR Consensus { get; init; }

    /// <summary>
    /// True if all columns have converged to the same representation.
    /// Convergence = average pairwise overlap exceeds threshold.
    /// </summary>
    public bool Converged { get; init; }

    /// <summary>
    /// Average pairwise similarity between column votes [0, 1].
    /// 1.0 = perfect agreement. 0.0 = no overlap.
    /// </summary>
    public float AgreementScore { get; init; }

    /// <summary>Number of voting iterations performed.</summary>
    public int Iterations { get; init; }
}
