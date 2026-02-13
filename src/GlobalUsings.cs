// ============================================================================
// CortexSharp — Global Using Directives
// ============================================================================
// Bridge between the new CortexSharp namespace structure and the existing
// HtmEnhanced.cs monolith. The SDR class lives in the old namespace and
// is the fundamental data type used everywhere. This global using makes it
// available to all new code without coupling every file to the old namespace.
//
// When the SDR class is eventually migrated to CortexSharp.Core, this
// single file is the only thing that needs to change.
// ============================================================================

global using SDR = HierarchicalTemporalMemory.Enhanced.SDR;
