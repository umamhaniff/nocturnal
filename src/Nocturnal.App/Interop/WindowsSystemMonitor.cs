using System.Runtime.InteropServices;
using Nocturnal.Core;

namespace Nocturnal.App.Interop;

public sealed class WindowsSystemMonitor : ISystemMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    private bool _acpiWmiUnavailable;

    public SystemPowerSnapshot GetSnapshot()
    {
        var isOnAc = true;
        var batteryPercent = 100;
        var hasBattery = false;

        if (GetSystemPowerStatus(out var status))
        {
            isOnAc = status.ACLineStatus != 0;
            hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent <= 100;
            if (status.BatteryLifePercent <= 100)
            {
                batteryPercent = status.BatteryLifePercent;
            }
        }

        var tempC = TryReadAcpiTemperatureCelsius();
        var thermal = tempC switch
        {
            null => ThermalLevel.Unknown,
            >= 95.0 => ThermalLevel.Critical,
            >= 85.0 => ThermalLevel.Hot,
            >= 75.0 => ThermalLevel.Warm,
            _ => ThermalLevel.Normal
        };

        return new SystemPowerSnapshot(isOnAc, batteryPercent, hasBattery, thermal, tempC);
    }

    private double? TryReadAcpiTemperatureCelsius()
    {
        if (_acpiWmiUnavailable)
        {
            return null;
        }

        object? locatorObj = null;
        object? servicesObj = null;
        object? zonesObj = null;

        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null)
            {
                _acpiWmiUnavailable = true;
                return null;
            }

            locatorObj = Activator.CreateInstance(locatorType)!;
            dynamic locator = locatorObj;
            servicesObj = locator.ConnectServer(".", @"root\WMI");
            dynamic services = servicesObj;
            zonesObj = services.ExecQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            dynamic zones = zonesObj;

            double? maxCelsius = null;
            foreach (var zone in zones)
            {
                try
                {
                    // ACPI reports tenths of Kelvin
                    double kelvinTenths = Convert.ToDouble(zone.CurrentTemperature);
                    var celsius = (kelvinTenths / 10.0) - 273.15;
                    if (celsius is > 0 and < 150)
                    {
                        maxCelsius = maxCelsius.HasValue ? Math.Max(maxCelsius.Value, celsius) : celsius;
                    }
                }
                finally
                {
                    ReleaseCom(zone);
                }
            }

            return maxCelsius;
        }
        catch
        {
            // Many Windows OEMs restrict MSAcpi_ThermalZoneTemperature; disable repeat WMI probes.
            _acpiWmiUnavailable = true;
            return null;
        }
        finally
        {
            ReleaseCom(zonesObj);
            ReleaseCom(servicesObj);
            ReleaseCom(locatorObj);
        }
    }

    private static void ReleaseCom(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj))
        {
            Marshal.FinalReleaseComObject(obj);
        }
    }
}
