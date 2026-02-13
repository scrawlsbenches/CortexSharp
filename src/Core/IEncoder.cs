// ============================================================================
// CortexSharp — Encoder Interface
// ============================================================================
// Encoders convert raw data into Sparse Distributed Representations (SDRs).
// They are the bridge between the external world and the cortical system.
//
// The fundamental contract:
//   Semantically similar inputs MUST produce SDRs with high bit overlap.
//   Semantically different inputs MUST produce SDRs with low/zero overlap.
//
// This is not optional — the entire downstream computation (SP, TM, CP)
// depends on this property. If the encoder doesn't preserve similarity
// in its output, no amount of learning can recover it.
//
// Examples:
//   ScalarEncoder:  5.0 and 5.1 share most bits; 5.0 and 50.0 share none
//   CategoryEncoder: "red" and "blue" share zero bits (categorical, no order)
//   CoordinateEncoder: nearby GPS coordinates share bits proportional to proximity
//
// Reference: BAMI (Biological and Machine Intelligence), Encoding chapter
// ============================================================================

namespace CortexSharp.Core;

/// <summary>
/// Encodes a value of type <typeparamref name="T"/> into an SDR.
/// The encoding must preserve semantic similarity as bit overlap.
/// </summary>
/// <typeparam name="T">The type of value to encode.</typeparam>
public interface IEncoder<in T>
{
    /// <summary>
    /// Encode a value into an SDR.
    /// </summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>SDR where similar values produce overlapping patterns.</returns>
    SDR Encode(T value);

    /// <summary>Total number of bits in the output SDR.</summary>
    int OutputSize { get; }

    /// <summary>Number of active bits (ones) in each output SDR.</summary>
    int ActiveBits { get; }
}
