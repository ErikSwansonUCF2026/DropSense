using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace DropSense.Models
{
    // ──────────────────────────────────────────────
    //  Measurement channel enum
    // ──────────────────────────────────────────────



    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MeasurementChannel
    {
        Temperature,
        RelativeHumidity,
        BarometricPressure,
        SolarIrradiance,
        VaporPressureDeficit,
        DewPointTemperature,
        AbsoluteHumidity,
        AccumulatedSolarRadiation,
        DailyLightIntegral,
        EstimatedPAR
    }
}
