// gate1: publicDtoType
// gate1-gameable: the type is renamed off the classification patterns. Nothing else changes — same
// public type, same public properties, same assembly, same consumers. It stops being scored because
// it stops being *recognised*, and it stops being recognised because a DTO is whatever ends in Dto,
// DTO, Info, Command, Result, Request, Response, Model or View.
//
// This is a bigger finding than the 5 points it moves, because publicDtoType is not the only charge
// that disappears. Classification is the gate on the entire durable-surface pass
// (SurfaceScoreEngine.DurableSurface.cs:18), so the rename also takes dtoScalarProperty,
// dtoCollectionProperty and dtoNestedProperty with it — the type leaves the score altogether. Every
// DTO rule shares one failure mode, and it is spelled with an identifier.
//
// A structural test would not have this problem: a public type that is all public properties and no
// behaviour is a data carrier whatever it is called, and LooksLikeDataCarrier already computes
// exactly that — it is applied *after* the name test rather than instead of it.

namespace SampleSolution.Gate.Rules;

public sealed class GateShipmentDto
{
    public int ShipmentId { get; set; }
    public string Carrier { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
}
