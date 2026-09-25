using System;

namespace Game.Net;

[Flags]
public enum Layer : uint
{
	Road = 1u,
	PowerlineLow = 2u,
	PowerlineHigh = 4u,
	WaterPipe = 8u,
	SewagePipe = 0x10u,
	StormwaterPipe = 0x20u,
	TrainTrack = 0x40u,
	Pathway = 0x80u,
	Waterway = 0x100u,
	Taxiway = 0x200u,
	TramTrack = 0x400u,
	SubwayTrack = 0x800u,
	Fence = 0x1000u,
	MarkerPathway = 0x2000u,
	MarkerTaxiway = 0x4000u,
	PublicTransportRoad = 0x8000u,
	LaneEditor = 0x10000u,
	ResourceLine = 0x20000u,
	NetFence = 0x40000u,
	None = 0u,
	All = uint.MaxValue
}
You are not using the latest version of the tool, please update.
Latest version is '11.0.0.9375' (yours is '9.0.0.7889')
