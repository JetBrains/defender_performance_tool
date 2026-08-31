using System;

namespace DefenderPerformanceTool;

public record EventInfo(double DurationMsec, string Process, DateTime Timestamp, string FilePath = "");
