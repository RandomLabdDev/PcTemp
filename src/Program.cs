using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PcTemp
{
    internal static class Program
    {
        internal const string AppVersion = "1.13.63";
        internal const string ContactEmail = "randomlabdev@gmail.com";
        internal const string ProjectUrl = "https://github.com/RandomLabdDev/PcTemp";
        private const string MutexName = "Local\\PcTemp_5D9232C8_57FD_47DB_AA68_F8BE5A2D9274";
        private const string ShowEventName = "Local\\PcTemp_Show_5D9232C8_57FD_47DB_AA68_F8BE5A2D9274";

        [STAThread]
        private static void Main(string[] args)
        {
            if (!IsCurrentProcessAdministrator())
            {
                try
                {
                    string arguments = string.Join(" ", args.Select(x => "\"" + x.Replace("\"", "\\\"") + "\""));
                    ProcessStartInfo start = new ProcessStartInfo(Application.ExecutablePath, arguments);
                    start.UseShellExecute = true;
                    start.Verb = "runas";
                    Process.Start(start);
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode != 1223)
                        MessageBox.Show("PcTemp necesita permisos de administrador para funcionar:\n" + ex.Message,
                            "PcTemp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                using (Icon icon = TrayIconFactory.Create("42", Color.DeepSkyBlue)) { }
                using (SensorReader reader = new SensorReader())
                {
                    TemperatureSnapshot test = reader.Read();
                    using (DashboardForm form = new DashboardForm(null, null, null))
                        form.UpdateValues(test, 5, false, IsCurrentProcessAdministrator(), SensorAccess.IsPawnIoInstalled());
                    Environment.ExitCode = (test.Cpu.HasValue ? 0 : 1) |
                                           (test.Gpu.HasValue ? 0 : 2) |
                                           (test.Board.HasValue ? 0 : 4);
                }
                return;
            }

            if (args.Length > 1 && string.Equals(args[0], "--diagnose", StringComparison.OrdinalIgnoreCase))
            {
                using (SensorReader reader = new SensorReader())
                {
                    reader.Read();
                    Thread.Sleep(750);
                    reader.Read();
                    File.WriteAllText(args[1], reader.GetReport());
                }
                return;
            }

            bool elevationRequested = args.Any(x => string.Equals(x, "--elevated", StringComparison.OrdinalIgnoreCase));
            bool startupLaunch = args.Any(x => string.Equals(x, "--startup", StringComparison.OrdinalIgnoreCase));
            bool ownsMutex = false;
            using (Mutex mutex = new Mutex(false, MutexName))
            {
                try
                {
                    try { ownsMutex = mutex.WaitOne(elevationRequested ? 15000 : 0, false); }
                    catch (AbandonedMutexException) { ownsMutex = true; }

                    if (!ownsMutex)
                    {
                        if (startupLaunch)
                            return;
                        try
                        {
                            using (EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName))
                                existing.Set();
                        }
                        catch
                        {
                            MessageBox.Show("PcTemp ya se está ejecutando.", "PcTemp",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        return;
                    }

                    using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                    {
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                        {
                            CrashReporter.Report(e.Exception, "Interfaz", "Excepción no controlada", null);
                            MessageBox.Show("PcTemp ha detectado un fallo inesperado. Si autorizaste los informes, se enviará una traza técnica anónima.\n\n" +
                                e.Exception.Message, "PcTemp", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        };
                        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                        {
                            Exception exception = e.ExceptionObject as Exception ?? new Exception("Error no controlado.");
                            CrashReporter.ReportFatal(exception, "Aplicación", "Cierre inesperado", null);
                        };
                        using (TrayApplication app = new TrayApplication(showEvent, startupLaunch))
                            Application.Run(app);
                    }
                }
                finally
                {
                    if (ownsMutex) mutex.ReleaseMutex();
                }
            }
        }

        private static bool IsCurrentProcessAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static class CrashReporter
    {
        private const string SettingsKey = @"Software\PcTemp";
        private const string Endpoint = "https://pctemp-reports.randomlabdev.workers.dev/v1/report";
        private static readonly object Sync = new object();
        private static readonly HashSet<string> SentSignatures = new HashSet<string>(StringComparer.Ordinal);

        internal static void Report(Exception exception, string component, string action, TemperatureSnapshot snapshot)
        {
            if (exception == null || !HasConsent()) return;
            if (!Reserve(exception, component, action)) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Send(exception, component, action, snapshot); }
                catch { }
            });
        }

        internal static void ReportFatal(Exception exception, string component, string action, TemperatureSnapshot snapshot)
        {
            if (exception == null || !HasConsent() || !Reserve(exception, component, action)) return;
            try { Send(exception, component, action, snapshot); }
            catch { }
        }

        private static bool Reserve(Exception exception, string component, string action)
        {
            string signature = exception.GetType().FullName + "|" + component + "|" + action;
            lock (Sync) return SentSignatures.Add(signature);
        }

        internal static bool ConsentEnabled
        {
            get { return HasConsent(); }
        }

        internal static void SetConsent(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    key.SetValue("SendErrorReports", enabled ? 1 : 0, RegistryValueKind.DWord);
                    if (enabled && key.GetValue("InstallationId") == null)
                        key.SetValue("InstallationId", Guid.NewGuid().ToString("D"), RegistryValueKind.String);
                }
            }
            catch { }
        }

        private static bool HasConsent()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                    return key != null && Convert.ToInt32(key.GetValue("SendErrorReports", 0), CultureInfo.InvariantCulture) != 0;
            }
            catch { return false; }
        }

        private static string InstallationId()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    string value = Convert.ToString(key.GetValue("InstallationId"), CultureInfo.InvariantCulture);
                    Guid id;
                    if (Guid.TryParse(value, out id)) return id.ToString("D");
                    value = Guid.NewGuid().ToString("D");
                    key.SetValue("InstallationId", value, RegistryValueKind.String);
                    return value;
                }
            }
            catch { return Guid.NewGuid().ToString("D"); }
        }

        private static void Send(Exception exception, string component, string action, TemperatureSnapshot snapshot)
        {
            string json = BuildJson(exception, component, action, snapshot);
            byte[] body = Encoding.UTF8.GetBytes(json);
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Endpoint);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.UserAgent = "PcTemp/" + Program.AppVersion;
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.ContentLength = body.Length;
            using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) { }
        }

        private static string BuildJson(Exception exception, string component, string action, TemperatureSnapshot snapshot)
        {
            StringBuilder json = new StringBuilder(4096);
            json.Append("{\"schema\":1,\"consent\":true");
            Add(json, "reportId", Guid.NewGuid().ToString("D"));
            Add(json, "installationId", InstallationId());
            Add(json, "appVersion", Program.AppVersion);
            Add(json, "timestampUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            json.Append(",\"system\":{");
            AddFirst(json, "osVersion", Environment.OSVersion.VersionString);
            Add(json, "architecture", Environment.Is64BitOperatingSystem ? "x64" : "x86");
            Add(json, "culture", CultureInfo.CurrentUICulture.Name);
            json.Append("},\"hardware\":{");
            AddFirst(json, "cpu", snapshot == null ? "" : snapshot.CpuName);
            Add(json, "gpu", snapshot == null || !snapshot.Gpu.HasValue ? "" : snapshot.GpuName);
            Add(json, "motherboard", snapshot == null ? "" : snapshot.BoardName);
            Add(json, "memory", snapshot == null ? "" : string.Join(", ", snapshot.Memories.Select(x => x.Name).Distinct().Take(4).ToArray()));
            json.Append("},\"exception\":{");
            AddFirst(json, "type", exception.GetType().FullName ?? exception.GetType().Name);
            Add(json, "message", Sanitize(exception.Message));
            Add(json, "stackTrace", Sanitize(exception.ToString()));
            json.Append("},\"context\":{");
            AddFirst(json, "component", component);
            Add(json, "action", action);
            json.Append("},\"sensors\":[");
            if (snapshot != null) AppendSensors(json, snapshot);
            json.Append("]}");
            return json.ToString();
        }

        private static void AppendSensors(StringBuilder json, TemperatureSnapshot snapshot)
        {
            bool first = true;
            AppendSensor(json, ref first, "CPU", snapshot.CpuSensor, snapshot.Cpu);
            AppendSensor(json, ref first, "GPU", snapshot.GpuSensor, snapshot.Gpu);
            AppendSensor(json, ref first, "Placa base", snapshot.BoardSensor, snapshot.Board);
            foreach (DiskTemperature disk in snapshot.Disks.Take(16))
                AppendSensor(json, ref first, "Disco", disk.Sensor, disk.Value);
            foreach (MemoryTemperature memory in snapshot.Memories.Take(12))
                AppendSensor(json, ref first, "RAM", memory.Sensor, memory.Value);
        }

        private static void AppendSensor(StringBuilder json, ref bool first, string component, string name, float? value)
        {
            if (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value)) return;
            if (!first) json.Append(',');
            first = false;
            json.Append('{');
            AddFirst(json, "component", component);
            Add(json, "name", name);
            json.Append(",\"value\":").Append(value.Value.ToString("0.0", CultureInfo.InvariantCulture));
            Add(json, "unit", "°C");
            json.Append('}');
        }

        private static string Sanitize(string value)
        {
            string result = value ?? "";
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(profile)) result = result.Replace(profile, "<perfil>");
                if (!string.IsNullOrWhiteSpace(Environment.UserName)) result = result.Replace(Environment.UserName, "<usuario>");
                if (!string.IsNullOrWhiteSpace(Environment.MachineName)) result = result.Replace(Environment.MachineName, "<equipo>");
            }
            catch { }
            return result;
        }

        private static void AddFirst(StringBuilder json, string name, string value)
        {
            json.Append('"').Append(Escape(name)).Append("\":\"").Append(Escape(value)).Append('"');
        }

        private static void Add(StringBuilder json, string name, string value)
        {
            json.Append(",\"").Append(Escape(name)).Append("\":\"").Append(Escape(value)).Append('"');
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            StringBuilder escaped = new StringBuilder(value.Length + 16);
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': escaped.Append("\\\""); break;
                    case '\\': escaped.Append("\\\\"); break;
                    case '\b': escaped.Append("\\b"); break;
                    case '\f': escaped.Append("\\f"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (character < 32) escaped.Append("\\u").Append(((int)character).ToString("x4"));
                        else escaped.Append(character);
                        break;
                }
            }
            return escaped.ToString();
        }
    }

    internal sealed class TemperatureSnapshot
    {
        public float? Cpu;
        public float? Gpu;
        public float? Board;
        public readonly List<DiskTemperature> Disks = new List<DiskTemperature>();
        public readonly List<MemoryTemperature> Memories = new List<MemoryTemperature>();
        public readonly List<BoardSensorReading> BoardSensors = new List<BoardSensorReading>();
        public string CpuName = "CPU";
        public string GpuName = "GPU";
        public string BoardName = "Placa base";
        public string CpuSensor = "No detectado";
        public string GpuSensor = "No detectado";
        public string BoardSensor = "No detectado";
        public string BoardSensorId = "";
        public string BoardSensorModeId = "";
        public DateTime UpdatedAt;
    }

    internal sealed class BoardSensorReading
    {
        public string Id;
        public string Name;
        public string HardwareName;
        public float Value;
        public int Score;
        public bool IsStable;
    }

    internal sealed class BoardSensorFilter
    {
        private const float MaximumImmediateValue = 100F;
        private const float MaximumImmediateChange = 15F;
        private const float PendingTolerance = 3F;
        private const int ConfirmationsRequired = 3;

        private sealed class State
        {
            public bool HasAccepted;
            public float Accepted;
            public float Pending;
            public int PendingCount;
        }

        private readonly Dictionary<string, State> _states =
            new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

        public float Filter(string id, float rawValue, out bool stable)
        {
            State state;
            if (!_states.TryGetValue(id, out state))
            {
                state = new State();
                _states[id] = state;
            }

            if (!state.HasAccepted)
            {
                if (rawValue <= MaximumImmediateValue)
                {
                    Accept(state, rawValue);
                    stable = true;
                    return state.Accepted;
                }

                TrackPending(state, rawValue);
                if (state.PendingCount >= ConfirmationsRequired)
                {
                    Accept(state, state.Pending);
                    stable = true;
                    return state.Accepted;
                }

                stable = false;
                return rawValue;
            }

            if (Math.Abs(rawValue - state.Accepted) <= MaximumImmediateChange)
            {
                Accept(state, rawValue);
                stable = true;
                return state.Accepted;
            }

            TrackPending(state, rawValue);
            if (state.PendingCount >= ConfirmationsRequired)
            {
                Accept(state, state.Pending);
                stable = true;
                return state.Accepted;
            }

            stable = false;
            return state.Accepted;
        }

        private static void TrackPending(State state, float value)
        {
            if (state.PendingCount > 0 && Math.Abs(value - state.Pending) <= PendingTolerance)
            {
                state.Pending = (state.Pending * state.PendingCount + value) / (state.PendingCount + 1);
                state.PendingCount++;
            }
            else
            {
                state.Pending = value;
                state.PendingCount = 1;
            }
        }

        private static void Accept(State state, float value)
        {
            state.HasAccepted = true;
            state.Accepted = value;
            state.Pending = 0F;
            state.PendingCount = 0;
        }
    }

    internal sealed class DiskTemperature
    {
        public string Id;
        public string Type;
        public string Interface;
        public string Name;
        public string Sensor;
        public string Status;
        public string Health;
        public float Value;
        public float? HealthPercent;
        public float? FreeSpaceGigabytes;
        public float? ActivityPercent;
        public float? ReadBytesPerSecond;
        public float? WriteBytesPerSecond;
        public ulong CapacityBytes;
    }

    internal sealed class MemoryTemperature
    {
        public string Id;
        public string Name;
        public string Sensor;
        public float Value;
        public ulong CapacityBytes;
        public string MemoryType;
        public uint SpeedMHz;
        public uint ConfiguredSpeedMHz;
        public uint ConfiguredVoltageMillivolts;
        public string Slot;
    }

    internal static class SensorCollection
    {
        public static bool Contains(List<DiskTemperature> items, string id)
        {
            foreach (DiskTemperature item in items)
            {
                if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool Contains(List<MemoryTemperature> items, string id)
        {
            foreach (MemoryTemperature item in items)
            {
                if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    internal sealed class SessionTemperatureStats
    {
        public float? Minimum { get; private set; }
        public float? Maximum { get; private set; }

        public void Update(float? value)
        {
            if (!value.HasValue) return;
            if (!Minimum.HasValue || value.Value < Minimum.Value) Minimum = value.Value;
            if (!Maximum.HasValue || value.Value > Maximum.Value) Maximum = value.Value;
        }
    }

    internal sealed class SensorSelection
    {
        public bool HasValue;
        public string Id;
        public string Name;
        public string SensorName;
        public string HardwareName;
        public float Value;
        public int Score;

        public void Reset()
        {
            HasValue = false;
            Id = null;
            Name = null;
            SensorName = null;
            HardwareName = null;
            Value = 0F;
            Score = 0;
        }

        public void Consider(string sensorName, string hardwareName, float value, int score, string id = null)
        {
            if (HasValue && (score < Score || (score == Score && value <= Value)))
                return;

            HasValue = true;
            Id = id;
            Name = hardwareName + " · " + sensorName;
            SensorName = sensorName;
            HardwareName = hardwareName;
            Value = value;
            Score = score;
        }
    }

    internal sealed class SensorReader : IDisposable
    {
        private readonly Computer _computer;
        private readonly List<PhysicalDiskInfo> _physicalDisks;
        private readonly List<PhysicalMemoryInfo> _physicalMemories;
        private readonly Dictionary<string, DiskMetadata> _diskMetadataCache =
            new Dictionary<string, DiskMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MemoryMetadata> _memoryMetadataCache =
            new Dictionary<string, MemoryMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly string _baseBoardName;
        private readonly SensorSelection _cpuSelection = new SensorSelection();
        private readonly SensorSelection _gpuSelection = new SensorSelection();
        private readonly SensorSelection _boardSelection = new SensorSelection();
        private readonly SensorSelection _boardCpuFallback = new SensorSelection();
        private readonly SensorSelection _componentSelection = new SensorSelection();
        private readonly BoardSensorFilter _boardFilter = new BoardSensorFilter();

        private sealed class PhysicalDiskInfo
        {
            public string DeviceId;
            public string Name;
            public ushort MediaType;
            public ushort BusType;
            public ushort HealthStatus;
            public ushort[] OperationalStatus;
            public ulong Size;
        }

        private sealed class PhysicalMemoryInfo
        {
            public string Manufacturer;
            public string PartNumber;
            public string DeviceLocator;
            public ulong Capacity;
            public uint Speed;
            public uint ConfiguredSpeed;
            public uint ConfiguredVoltage;
            public ushort MemoryType;
        }

        private sealed class DiskMetadata
        {
            public string DeviceId;
            public string Type;
            public string Interface;
            public string Status;
            public string Health;
            public ulong Capacity;
        }

        private sealed class DiskPerformance
        {
            public float Activity;
            public float ReadBytesPerSecond;
            public float WriteBytesPerSecond;
        }

        private sealed class MemoryMetadata
        {
            public ulong Capacity;
            public string MemoryType;
            public uint Speed;
            public uint ConfiguredSpeed;
            public uint ConfiguredVoltage;
            public string Slot;
        }

        public SensorReader()
        {
            _physicalDisks = LoadPhysicalDiskInfo();
            _physicalMemories = LoadPhysicalMemoryInfo();
            _baseBoardName = LoadBaseBoardName();
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true
            };
            _computer.Open();
        }

        public TemperatureSnapshot Read(string preferredBoardSensorId = null)
        {
            TemperatureSnapshot result = new TemperatureSnapshot();
            result.BoardSensorModeId = preferredBoardSensorId ?? "";
            _cpuSelection.Reset();
            _gpuSelection.Reset();
            _boardSelection.Reset();
            _boardCpuFallback.Reset();
            Dictionary<string, DiskPerformance> diskPerformance = LoadDiskPerformance();

            foreach (IHardware hardware in _computer.Hardware)
            {
                UpdateTree(hardware);
                if (hardware.HardwareType == HardwareType.Cpu)
                    Collect(hardware, _cpuSelection, ComponentKind.Cpu, null);
                else if (hardware.HardwareType == HardwareType.GpuNvidia ||
                         hardware.HardwareType == HardwareType.GpuAmd ||
                         hardware.HardwareType == HardwareType.GpuIntel)
                    Collect(hardware, _gpuSelection, ComponentKind.Gpu, null);
                else if (hardware.HardwareType == HardwareType.Motherboard)
                    CollectBoard(hardware, result);
                else if (hardware.HardwareType == HardwareType.Memory)
                {
                    _componentSelection.Reset();
                    Collect(hardware, _componentSelection, ComponentKind.Memory, null);
                    if (_componentSelection.HasValue)
                    {
                        string memoryId = "memory:" + hardware.Identifier;
                        MemoryMetadata metadata;
                        if (!_memoryMetadataCache.TryGetValue(memoryId, out metadata))
                        {
                            metadata = GetMemoryMetadata(hardware.Name, _componentSelection.SensorName);
                            _memoryMetadataCache[memoryId] = metadata;
                        }
                        result.Memories.Add(new MemoryTemperature
                        {
                            Id = memoryId,
                            Name = hardware.Name,
                            Sensor = _componentSelection.Name,
                            Value = _componentSelection.Value,
                            CapacityBytes = metadata.Capacity,
                            MemoryType = metadata.MemoryType,
                            SpeedMHz = metadata.Speed,
                            ConfiguredSpeedMHz = metadata.ConfiguredSpeed,
                            ConfiguredVoltageMillivolts = metadata.ConfiguredVoltage,
                            Slot = metadata.Slot
                        });
                    }
                }
                else if (hardware.HardwareType == HardwareType.Storage)
                {
                    _componentSelection.Reset();
                    Collect(hardware, _componentSelection, ComponentKind.Disk, null);
                    if (_componentSelection.HasValue)
                    {
                        string diskId = hardware.Identifier.ToString();
                        DiskMetadata metadata;
                        if (!_diskMetadataCache.TryGetValue(diskId, out metadata))
                        {
                            metadata = GetDiskMetadata(hardware.Name, diskId);
                            _diskMetadataCache[diskId] = metadata;
                        }
                        DiskTemperature disk = new DiskTemperature
                        {
                            Id = diskId,
                            Type = metadata.Type,
                            Interface = metadata.Interface,
                            Name = hardware.Name,
                            Sensor = _componentSelection.Name,
                            Status = metadata.Status,
                            Health = metadata.Health,
                            Value = _componentSelection.Value,
                            CapacityBytes = metadata.Capacity
                        };
                        CollectDiskMetrics(hardware, disk);
                        DiskPerformance performance;
                        if (!string.IsNullOrWhiteSpace(metadata.DeviceId) &&
                            diskPerformance.TryGetValue(metadata.DeviceId, out performance))
                        {
                            if (!disk.ActivityPercent.HasValue) disk.ActivityPercent = performance.Activity;
                            if (!disk.ReadBytesPerSecond.HasValue)
                                disk.ReadBytesPerSecond = performance.ReadBytesPerSecond;
                            if (!disk.WriteBytesPerSecond.HasValue)
                                disk.WriteBytesPerSecond = performance.WriteBytesPerSecond;
                        }
                        result.Disks.Add(disk);
                    }
                }
            }

            SensorSelection bestCpu = _cpuSelection.HasValue ? _cpuSelection : _boardCpuFallback;
            SensorSelection selectedBoard = _boardSelection;
            if (!string.IsNullOrWhiteSpace(preferredBoardSensorId))
            {
                BoardSensorReading preferred = result.BoardSensors.FirstOrDefault(item =>
                    string.Equals(item.Id, preferredBoardSensorId, StringComparison.OrdinalIgnoreCase));
                if (preferred != null)
                {
                    selectedBoard = new SensorSelection();
                    selectedBoard.Consider(preferred.Name, preferred.HardwareName,
                        preferred.Value, 1000, preferred.Id);
                }
            }
            Assign(bestCpu, out result.Cpu, out result.CpuSensor);
            Assign(_gpuSelection, out result.Gpu, out result.GpuSensor);
            Assign(selectedBoard, out result.Board, out result.BoardSensor);
            if (selectedBoard.HasValue) result.BoardSensor = selectedBoard.SensorName;
            result.BoardSensorId = selectedBoard.HasValue ? (selectedBoard.Id ?? "") : "";
            if (bestCpu.HasValue) result.CpuName = bestCpu.HardwareName;
            if (_gpuSelection.HasValue) result.GpuName = _gpuSelection.HardwareName;
            result.BoardName = !string.IsNullOrWhiteSpace(_baseBoardName)
                ? _baseBoardName
                : (!selectedBoard.HasValue || string.IsNullOrWhiteSpace(selectedBoard.HardwareName)
                    ? "Placa base" : selectedBoard.HardwareName);
            result.UpdatedAt = DateTime.Now;
            return result;
        }

        public string GetReport()
        {
            return _computer.GetReport();
        }

        private static void UpdateTree(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware child in hardware.SubHardware)
                UpdateTree(child);
        }

        private static void Collect(IHardware hardware, SensorSelection target, ComponentKind kind,
            SensorSelection boardCpuFallback)
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue)
                    continue;

                float value = sensor.Value.Value;
                if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0 || value > 125)
                    continue;

                target.Consider(sensor.Name, hardware.Name, value, ScoreSensor(sensor.Name, kind));
                if (boardCpuFallback != null &&
                    ((sensor.Name ?? "").IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     (hardware.Name ?? "").IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0))
                    boardCpuFallback.Consider(sensor.Name, hardware.Name, value, 55);
            }

            foreach (IHardware child in hardware.SubHardware)
                Collect(child, target, kind, boardCpuFallback);
        }

        private static void CollectDiskMetrics(IHardware hardware, DiskTemperature disk)
        {
            disk.FreeSpaceGigabytes = FindSensorValue(hardware, SensorType.Data,
                "Free Space", "Available Space");
            disk.ActivityPercent = FindSensorValue(hardware, SensorType.Load,
                "Total Activity", "Disk Activity", "Activity");
            disk.ReadBytesPerSecond = FindSensorValue(hardware, SensorType.Throughput,
                "Read Rate", "Read Throughput");
            disk.WriteBytesPerSecond = FindSensorValue(hardware, SensorType.Throughput,
                "Write Rate", "Write Throughput");
            disk.HealthPercent = FindSensorValue(hardware, SensorType.Level,
                "Remaining Life", "Health", "Remaining Life Percentage");
            if (disk.ActivityPercent.HasValue)
                disk.ActivityPercent = Math.Max(0F, Math.Min(100F, disk.ActivityPercent.Value));
            if (disk.HealthPercent.HasValue)
                disk.HealthPercent = Math.Max(0F, Math.Min(100F, disk.HealthPercent.Value));
        }

        private static float? FindSensorValue(IHardware hardware, SensorType type,
            params string[] preferredNames)
        {
            foreach (string preferred in preferredNames)
            {
                foreach (ISensor sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == type && sensor.Value.HasValue &&
                        string.Equals(sensor.Name, preferred, StringComparison.OrdinalIgnoreCase))
                    {
                        float value = sensor.Value.Value;
                        if (!float.IsNaN(value) && !float.IsInfinity(value) && value >= 0F)
                            return value;
                    }
                }
                foreach (IHardware child in hardware.SubHardware)
                {
                    float? value = FindSensorValue(child, type, preferred);
                    if (value.HasValue) return value;
                }
            }
            return null;
        }

        private void CollectBoard(IHardware hardware, TemperatureSnapshot result)
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue)
                    continue;

                float rawValue = sensor.Value.Value;
                if (float.IsNaN(rawValue) || float.IsInfinity(rawValue) || rawValue <= 0 || rawValue > 125)
                    continue;

                string sensorName = (sensor.Name ?? "").Trim();
                string hardwareName = (hardware.Name ?? "").Trim();
                string sensorId = sensor.Identifier.ToString();
                bool stable;
                float value = _boardFilter.Filter(sensorId, rawValue, out stable);

                if (IsBoardSensorChoice(sensorName))
                {
                    result.BoardSensors.Add(new BoardSensorReading
                    {
                        Id = sensorId,
                        Name = sensorName,
                        HardwareName = hardwareName,
                        Value = value,
                        Score = ScoreSensor(sensorName, ComponentKind.Board),
                        IsStable = stable
                    });
                }

                bool generic = IsGenericBoardSensor(sensorName);
                if (!generic && value <= 100F)
                {
                    int score = ScoreSensor(sensorName, ComponentKind.Board);
                    if (!_boardSelection.HasValue || score > _boardSelection.Score)
                        _boardSelection.Consider(sensorName, hardwareName, value, score, sensorId);
                }

                if (sensorName.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    hardwareName.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0)
                    _boardCpuFallback.Consider(sensorName, hardwareName, value, 55, sensorId);
            }

            foreach (IHardware child in hardware.SubHardware)
                CollectBoard(child, result);
        }

        private static bool IsGenericBoardSensor(string sensorName)
        {
            string name = (sensorName ?? "").Trim().ToLowerInvariant();
            return name == "temperature" || name.StartsWith("temperature #") ||
                name.StartsWith("temp #") || name.StartsWith("sensor #") ||
                name.Contains("auxtin");
        }

        private static bool IsBoardSensorChoice(string sensorName)
        {
            if (IsGenericBoardSensor(sensorName)) return false;
            string name = (sensorName ?? "").Trim().ToLowerInvariant();
            return name.Contains("motherboard") || name.Contains("mainboard") ||
                name.Contains("system") || name.Contains("board") ||
                name.Contains("chipset") || name == "pch" || name.StartsWith("pch ") ||
                name.Contains("vrm") || name.Contains("mos");
        }

        private static int ScoreSensor(string sensorName, ComponentKind kind)
        {
            string name = sensorName.ToLowerInvariant();
            if (kind == ComponentKind.Cpu)
            {
                if (name.Contains("package")) return 100;
                if (name.Contains("tctl") || name.Contains("tdie")) return 95;
                if (name.Contains("core max")) return 90;
                if (name == "cpu core" || name == "cpu") return 85;
                if (name.Contains("average")) return 80;
                return 30;
            }

            if (kind == ComponentKind.Gpu)
            {
                if (name == "gpu core") return 100;
                if (name.Contains("gpu core")) return 95;
                if (name.Contains("hot spot") || name.Contains("hotspot")) return 70;
                if (name.Contains("memory")) return 60;
                return 40;
            }

            if (kind == ComponentKind.Disk)
            {
                if (name == "temperature") return 100;
                if (name.Contains("composite")) return 95;
                if (name.Contains("drive") && name.Contains("temperature")) return 90;
                if (name.Contains("temperature")) return 85;
                return 50;
            }

            if (kind == ComponentKind.Memory)
            {
                if (name == "temperature") return 100;
                if (name.Contains("dimm") || name.Contains("module")) return 95;
                if (name.Contains("temperature")) return 90;
                return 60;
            }

            if (name == "motherboard temperature" || name == "motherboard" ||
                name == "mainboard temperature" || name == "mainboard") return 150;
            if (name.Contains("motherboard") || name.Contains("mainboard")) return 140;
            if (name == "system" || name == "system temperature") return 125;
            if (name.Contains("system")) return 120;
            if (name.Contains("board")) return 115;
            if (name.Contains("chipset") || name == "pch" || name.StartsWith("pch ")) return 100;
            if (name.Contains("vrm") || name.Contains("mos")) return 90;
            if (name.Contains("cpu")) return 20;
            if (IsGenericBoardSensor(name)) return 0;
            return 40;
        }

        private static string LoadBaseBoardName()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    string manufacturer = Convert.ToString(key == null ? null : key.GetValue("BaseBoardManufacturer"));
                    string product = Convert.ToString(key == null ? null : key.GetValue("BaseBoardProduct"));
                    string combined = ((manufacturer ?? "") + " " + (product ?? "")).Trim();
                    if (combined.Length > 0) return combined;
                }
            }
            catch { }
            return "";
        }

        private static List<PhysicalDiskInfo> LoadPhysicalDiskInfo()
        {
            List<PhysicalDiskInfo> result = new List<PhysicalDiskInfo>();
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    scope, new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, " +
                        "Size, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk")))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                    {
                        result.Add(new PhysicalDiskInfo
                        {
                            DeviceId = Convert.ToString(item["DeviceId"], CultureInfo.InvariantCulture),
                            Name = Convert.ToString(item["FriendlyName"]),
                            MediaType = Convert.ToUInt16(item["MediaType"], CultureInfo.InvariantCulture),
                            BusType = Convert.ToUInt16(item["BusType"], CultureInfo.InvariantCulture),
                            HealthStatus = Convert.ToUInt16(item["HealthStatus"], CultureInfo.InvariantCulture),
                            OperationalStatus = ToUShortArray(item["OperationalStatus"]),
                            Size = Convert.ToUInt64(item["Size"], CultureInfo.InvariantCulture)
                        });
                        item.Dispose();
                    }
                }
            }
            catch { }
            return result;
        }

        private static ushort[] ToUShortArray(object value)
        {
            Array items = value as Array;
            if (items == null) return new ushort[0];
            ushort[] result = new ushort[items.Length];
            for (int index = 0; index < items.Length; index++)
                result[index] = Convert.ToUInt16(items.GetValue(index), CultureInfo.InvariantCulture);
            return result;
        }

        private static Dictionary<string, DiskPerformance> LoadDiskPerformance()
        {
            Dictionary<string, DiskPerformance> result =
                new Dictionary<string, DiskPerformance>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    @"root\CIMV2", "SELECT Name, PercentDiskTime, DiskReadBytesPersec, " +
                        "DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                    {
                        string name = Convert.ToString(item["Name"]);
                        if (string.IsNullOrWhiteSpace(name) || name == "_Total")
                        {
                            item.Dispose();
                            continue;
                        }
                        int separator = name.IndexOf(' ');
                        string deviceId = separator < 0 ? name.Trim() : name.Substring(0, separator).Trim();
                        result[deviceId] = new DiskPerformance
                        {
                            Activity = Math.Max(0F, Math.Min(100F, ToSingle(item["PercentDiskTime"]))),
                            ReadBytesPerSecond = Math.Max(0F, ToSingle(item["DiskReadBytesPersec"])),
                            WriteBytesPerSecond = Math.Max(0F, ToSingle(item["DiskWriteBytesPersec"]))
                        };
                        item.Dispose();
                    }
                }
            }
            catch { }
            return result;
        }

        private static float ToSingle(object value)
        {
            try { return value == null ? 0F : Convert.ToSingle(value, CultureInfo.InvariantCulture); }
            catch { return 0F; }
        }

        private static List<PhysicalMemoryInfo> LoadPhysicalMemoryInfo()
        {
            List<PhysicalMemoryInfo> result = new List<PhysicalMemoryInfo>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    @"root\CIMV2", "SELECT Manufacturer, PartNumber, DeviceLocator, Capacity, Speed, " +
                        "ConfiguredClockSpeed, ConfiguredVoltage, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                    {
                        result.Add(new PhysicalMemoryInfo
                        {
                            Manufacturer = Convert.ToString(item["Manufacturer"]),
                            PartNumber = Convert.ToString(item["PartNumber"]),
                            DeviceLocator = Convert.ToString(item["DeviceLocator"]),
                            Capacity = Convert.ToUInt64(item["Capacity"], CultureInfo.InvariantCulture),
                            Speed = ToUInt32(item["Speed"]),
                            ConfiguredSpeed = ToUInt32(item["ConfiguredClockSpeed"]),
                            ConfiguredVoltage = ToUInt32(item["ConfiguredVoltage"]),
                            MemoryType = ToUInt16(item["SMBIOSMemoryType"])
                        });
                        item.Dispose();
                    }
                }
            }
            catch { }
            return result;
        }

        private static uint ToUInt32(object value)
        {
            try { return value == null ? 0U : Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0U; }
        }

        private static ushort ToUInt16(object value)
        {
            try { return value == null ? (ushort)0 : Convert.ToUInt16(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private DiskMetadata GetDiskMetadata(string model, string identifier)
        {
            string normalizedModel = NormalizeDiskName(model);
            PhysicalDiskInfo info = _physicalDisks.FirstOrDefault(x =>
            {
                string normalizedPhysical = NormalizeDiskName(x.Name);
                return normalizedPhysical.Length > 3 &&
                    (normalizedModel.Contains(normalizedPhysical) || normalizedPhysical.Contains(normalizedModel));
            });

            if (info != null)
            {
                string type = "";
                if (info.BusType == 17) type = "M2";
                else if (info.BusType == 7) type = "USB";
                else if (info.MediaType == 4) type = info.BusType == 11 ? "SSD SATA" : "SSD";
                else if (info.MediaType == 3) type = "HDD";
                else if (info.BusType == 11) type = "SATA";
                return new DiskMetadata
                {
                    DeviceId = info.DeviceId,
                    Type = type,
                    Interface = FormatDiskInterface(info.BusType),
                    Status = FormatOperationalStatus(info.OperationalStatus),
                    Health = FormatHealthStatus(info.HealthStatus),
                    Capacity = info.Size
                };
            }

            string id = (identifier ?? "").ToLowerInvariant();
            string upper = (model ?? "").ToUpperInvariant();
            string fallbackType = "";
            if (id.Contains("/nvme/")) fallbackType = "M2";
            else if (upper.Contains("SSD") || upper.Contains("SOLID STATE")) fallbackType = "SSD SATA";
            else if ((upper.StartsWith("WDC WD") || upper.StartsWith("ST")) && upper.Any(char.IsDigit)) fallbackType = "HDD";
            else if (id.Contains("/ahci/")) fallbackType = "SATA";
            return new DiskMetadata
            {
                DeviceId = ExtractStorageDeviceId(identifier),
                Type = fallbackType,
                Interface = id.Contains("/nvme/") ? "NVMe / PCIe" :
                    (id.Contains("/ahci/") ? "SATA" : "No disponible"),
                Status = "No disponible",
                Health = "No disponible",
                Capacity = 0
            };
        }

        private static string ExtractStorageDeviceId(string identifier)
        {
            string value = identifier ?? "";
            int slash = value.LastIndexOf('/');
            if (slash < 0 || slash >= value.Length - 1) return "";
            string candidate = value.Substring(slash + 1);
            return candidate.All(char.IsDigit) ? candidate : "";
        }

        private MemoryMetadata GetMemoryMetadata(string hardwareName, string sensorName)
        {
            string normalizedHardware = NormalizeDiskName(hardwareName);
            PhysicalMemoryInfo exact = _physicalMemories.FirstOrDefault(memory =>
            {
                string part = NormalizeDiskName(memory.PartNumber);
                return part.Length > 3 && normalizedHardware.Contains(part);
            });

            if (exact == null) exact = _physicalMemories.FirstOrDefault(memory =>
            {
                string brand = NormalizeDiskName(memory.Manufacturer);
                return brand.Length > 2 && normalizedHardware.Contains(brand);
            });
            if (exact == null && _physicalMemories.Count == 1) exact = _physicalMemories[0];
            if (exact == null) return new MemoryMetadata { MemoryType = "No disponible" };
            return new MemoryMetadata
            {
                Capacity = exact.Capacity,
                MemoryType = FormatMemoryType(exact.MemoryType),
                Speed = exact.Speed,
                ConfiguredSpeed = exact.ConfiguredSpeed,
                ConfiguredVoltage = exact.ConfiguredVoltage,
                Slot = ExtractDimmLabel(sensorName, exact.DeviceLocator)
            };
        }

        private static string ExtractDimmLabel(string sensorName, string fallback)
        {
            string value = sensorName ?? "";
            int marker = value.IndexOf("DIMM #", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                int end = marker + 6;
                while (end < value.Length && char.IsDigit(value[end])) end++;
                if (end > marker + 6) return value.Substring(marker, end - marker);
            }
            return string.IsNullOrWhiteSpace(fallback) ? "No disponible" : fallback.Trim();
        }

        private static string FormatDiskInterface(ushort busType)
        {
            switch (busType)
            {
                case 17: return "NVMe / PCIe";
                case 11: return "SATA";
                case 7: return "USB";
                case 3: return "ATA";
                case 8: return "RAID";
                case 10: return "SAS";
                case 12: return "SD";
                case 13: return "MMC";
                default: return "No disponible";
            }
        }

        private static string FormatHealthStatus(ushort status)
        {
            if (status == 0) return "Correcta";
            if (status == 1) return "Precaución";
            if (status == 2) return "Crítica";
            return "Desconocida";
        }

        private static string FormatOperationalStatus(ushort[] statuses)
        {
            if (statuses == null || statuses.Length == 0) return "No disponible";
            if (statuses.Contains((ushort)2)) return "En línea";
            if (statuses.Contains((ushort)3)) return "Degradado";
            if (statuses.Contains((ushort)6)) return "Con error";
            if (statuses.Contains((ushort)7)) return "No recuperable";
            if (statuses.Contains((ushort)8)) return "Iniciando";
            if (statuses.Contains((ushort)9)) return "Deteniéndose";
            if (statuses.Contains((ushort)12)) return "Sin conexión";
            return "Desconocido";
        }

        private static string FormatMemoryType(ushort type)
        {
            switch (type)
            {
                case 20: return "DDR";
                case 21: return "DDR2";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 34: return "DDR5";
                default: return "No disponible";
            }
        }

        private static string NormalizeDiskName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return new string(value.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        private static void Assign(SensorSelection candidate, out float? value, out string name)
        {
            if (candidate == null || !candidate.HasValue)
            {
                value = null;
                name = "No detectado";
            }
            else
            {
                value = candidate.Value;
                name = candidate.Name;
            }
        }

        public void Dispose()
        {
            _computer.Close();
        }

        private enum ComponentKind { Cpu, Gpu, Board, Disk, Memory }
    }

    internal sealed class TrayApplication : ApplicationContext
    {
        private const string SettingsKey = @"Software\PcTemp";
        private const string LegacySettingsKey = @"Software\TempBandeja";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "PcTemp";
        private const string LegacyRunValue = "TempBandeja";

        private readonly NotifyIcon _cpuIcon;
        private readonly NotifyIcon _gpuIcon;
        private readonly NotifyIcon _boardIcon;
        private readonly Dictionary<string, NotifyIcon> _diskIcons = new Dictionary<string, NotifyIcon>();
        private readonly Dictionary<string, NotifyIcon> _memoryIcons = new Dictionary<string, NotifyIcon>();
        private readonly Dictionary<NotifyIcon, string> _iconSignatures = new Dictionary<NotifyIcon, string>();
        private readonly Dictionary<string, bool> _traySelectionCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly SessionTemperatureStats _cpuStats = new SessionTemperatureStats();
        private readonly SessionTemperatureStats _gpuStats = new SessionTemperatureStats();
        private readonly SessionTemperatureStats _boardStats = new SessionTemperatureStats();
        private readonly Dictionary<string, SessionTemperatureStats> _diskStats = new Dictionary<string, SessionTemperatureStats>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SessionTemperatureStats> _memoryStats = new Dictionary<string, SessionTemperatureStats>(StringComparer.OrdinalIgnoreCase);
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _cpuStatus;
        private readonly ToolStripMenuItem _gpuStatus;
        private readonly ToolStripMenuItem _boardStatus;
        private readonly ToolStripMenuItem _diskStatus;
        private readonly ToolStripMenuItem _memoryStatus;
        private readonly Dictionary<string, ToolStripMenuItem> _diskMenuItems = new Dictionary<string, ToolStripMenuItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ToolStripMenuItem> _memoryMenuItems = new Dictionary<string, ToolStripMenuItem>(StringComparer.OrdinalIgnoreCase);
        private readonly ToolStripMenuItem _intervalMenu;
        private readonly ToolStripMenuItem _startupItem;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly EventWaitHandle _showEvent;
        private readonly RegisteredWaitHandle _showRequestRegistration;
        private readonly object _readerSync = new object();
        private SensorReader _reader;
        private readonly DashboardForm _dashboard;
        private readonly bool _isAdministrator;
        private readonly bool _isPawnIoInstalled;
        private TemperatureSnapshot _snapshot = new TemperatureSnapshot();
        private int _intervalSeconds;
        private string _boardSensorSelection;
        private int _reading;
        private bool _closing;
        private bool _trayReady;
        private readonly bool _startupLaunch;
        private bool _startupEnabled;
        private bool _updatingTraySelectionUi;

        public TrayApplication(EventWaitHandle showEvent, bool startupLaunch)
        {
            _showEvent = showEvent;
            _startupLaunch = startupLaunch;
            _isAdministrator = IsAdministrator();
            _isPawnIoInstalled = SensorAccess.IsPawnIoInstalled();
            MigrateLegacySettings();
            _intervalSeconds = LoadInterval();
            _boardSensorSelection = LoadBoardSensorSelection();
            EnsureFirstRunStartup();
            UpgradeStartupRegistration();
            _startupEnabled = IsStartupEnabled();

            _dashboard = new DashboardForm(InstallSensorDriver, RequestElevation, OpenTraySettings,
                IsTraySelectionEnabled, SetTraySelection, DashboardStartupChanged, CardColorChanged,
                BoardSensorChanged);
            _dashboard.FormClosing += DashboardClosing;
            IntPtr dashboardHandle = _dashboard.Handle;
            _showRequestRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showEvent, ShowRequestSignaled, null, Timeout.Infinite, false);

            _menu = new ContextMenuStrip();
            ToolStripMenuItem title = new ToolStripMenuItem("PCTEMP " + Program.AppVersion) { Enabled = false };
            _cpuStatus = CreateMainSensorMenuItem("CPU: leyendo…", "cpu");
            _gpuStatus = CreateMainSensorMenuItem("GPU: leyendo…", "gpu");
            _boardStatus = CreateMainSensorMenuItem("Placa: leyendo…", "board");
            _diskStatus = new ToolStripMenuItem("Discos: leyendo…") { Enabled = false };
            _memoryStatus = new ToolStripMenuItem("Memoria RAM: buscando sensores…") { Enabled = false };
            ToolStripMenuItem open = new ToolStripMenuItem("Abrir panel");
            open.Click += delegate { ShowDashboard(); };
            ToolStripMenuItem refresh = new ToolStripMenuItem("Actualizar ahora");
            refresh.Click += delegate { BeginRefreshTemperatures(); };

            _intervalMenu = new ToolStripMenuItem("Intervalo de actualización");
            foreach (int seconds in new[] { 2, 5, 10, 30, 60 })
            {
                int captured = seconds;
                ToolStripMenuItem item = new ToolStripMenuItem(seconds + " segundos");
                item.Tag = seconds;
                item.Click += delegate { SetInterval(captured); };
                _intervalMenu.DropDownItems.Add(item);
            }

            _startupItem = new ToolStripMenuItem("Iniciar con Windows") { CheckOnClick = true };
            _startupItem.Checked = _startupEnabled;
            _startupItem.Click += StartupClicked;
            ToolStripMenuItem exit = new ToolStripMenuItem("Salir");
            exit.Click += delegate { CloseApplication(); };

            _menu.Items.AddRange(new ToolStripItem[]
            {
                title, new ToolStripSeparator(), _cpuStatus, _gpuStatus, _boardStatus, _memoryStatus, _diskStatus,
                new ToolStripSeparator(), open, refresh, _intervalMenu, _startupItem,
                new ToolStripSeparator(), exit
            });

            // Un único icono grande por sensor, mostrando solamente la temperatura.
            // El nombre completo aparece al pasar el ratón.
            _boardIcon = CreateNotifyIcon("Placa base", DashboardForm.LoadCardColor("board", Color.Gold));
            _gpuIcon = CreateNotifyIcon("GPU", DashboardForm.LoadCardColor("gpu", Color.Gainsboro));
            _cpuIcon = CreateNotifyIcon("CPU", DashboardForm.LoadCardColor("cpu", Color.DeepSkyBlue));

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _intervalSeconds * 1000;
            _timer.Tick += delegate { BeginRefreshTemperatures(); };
            UpdateIntervalChecks();
            Application.Idle += FirstApplicationIdle;
        }

        private ToolStripMenuItem CreateMainSensorMenuItem(string text, string id)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text)
            {
                CheckOnClick = true,
                Checked = IsTraySelectionEnabled(id, true),
                Tag = id
            };
            item.CheckedChanged += SensorMenuCheckedChanged;
            return item;
        }

        private NotifyIcon CreateNotifyIcon(string label, Color color)
        {
            NotifyIcon icon = new NotifyIcon();
            icon.ContextMenuStrip = _menu;
            icon.Text = "PcTemp · " + label + ": leyendo…";
            icon.Icon = TrayIconFactory.Create("--", color);
            _iconSignatures[icon] = IconSignature("--", color);
            icon.Visible = false;
            icon.DoubleClick += delegate { ShowDashboard(); };
            icon.BalloonTipClicked += delegate { ShowDashboard(); };
            return icon;
        }

        private void FirstApplicationIdle(object sender, EventArgs e)
        {
            Application.Idle -= FirstApplicationIdle;
            _trayReady = true;
            ApplyMainTrayVisibility();
            _timer.Start();
            if (!_startupLaunch)
                ShowStartupConfirmation();
            BeginRefreshTemperatures();
        }

        private void ShowStartupConfirmation()
        {
            bool firstIntroduction = false;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                object versionValue = key.GetValue("IntroVersion");
                int version;
                if (versionValue == null || !int.TryParse(versionValue.ToString(), out version) || version < 3)
                {
                    key.SetValue("IntroVersion", 3, RegistryValueKind.DWord);
                    firstIntroduction = true;
                }
            }

            _cpuIcon.BalloonTipIcon = ToolTipIcon.Info;
            _cpuIcon.BalloonTipTitle = "PcTemp está activo";
            _cpuIcon.BalloonTipText = "CPU, GPU y placa base ya aparecen como tres números. Si Windows los oculta, abre ^ y arrástralos a la barra.";
            _cpuIcon.ShowBalloonTip(7000);
            if (firstIntroduction)
                ShowDashboard();
        }

        private void BeginRefreshTemperatures()
        {
            if (_closing || Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
                return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                TemperatureSnapshot result = null;
                string error = null;
                try
                {
                    lock (_readerSync)
                    {
                        if (_reader == null)
                            _reader = new SensorReader();
                        result = _reader.Read(_boardSensorSelection);
                    }
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                    CrashReporter.Report(ex, "Sensores", "Actualizar temperaturas", _snapshot);
                }

                if (_closing)
                {
                    Interlocked.Exchange(ref _reading, 0);
                    DisposeSensorReader();
                    return;
                }

                try
                {
                    _dashboard.BeginInvoke(new MethodInvoker(delegate
                    {
                        try { ApplyTemperatureResult(result, error); }
                        finally
                        {
                            Interlocked.Exchange(ref _reading, 0);
                            if (_closing) DisposeSensorReader();
                        }
                    }));
                }
                catch
                {
                    Interlocked.Exchange(ref _reading, 0);
                    if (_closing) DisposeSensorReader();
                }
            });
        }

        private void DisposeSensorReader()
        {
            lock (_readerSync)
            {
                if (_reader == null) return;
                _reader.Dispose();
                _reader = null;
            }
        }

        private void ApplyTemperatureResult(TemperatureSnapshot result, string error)
        {
            if (_closing) return;
            if (error == null && result != null)
            {
                _snapshot = result;
                UpdateSessionStatistics(_snapshot);
                UpdateIcon(_cpuIcon, "CPU", _snapshot.Cpu, DashboardForm.LoadCardColor("cpu", Color.DeepSkyBlue));
                UpdateIcon(_gpuIcon, "GPU", _snapshot.Gpu, DashboardForm.LoadCardColor("gpu", Color.Gainsboro));
                UpdateIcon(_boardIcon, "Placa base · " + _snapshot.BoardSensor, _snapshot.Board,
                    DashboardForm.LoadCardColor("board", Color.Gold));
                _cpuStatus.Text = FormatSessionTemperature("CPU", _snapshot.Cpu, _cpuStats);
                _gpuStatus.Text = FormatSessionTemperature("GPU", _snapshot.Gpu, _gpuStats);
                _boardStatus.Text = FormatSessionTemperature("Placa base", _snapshot.Board, _boardStats);
                _boardStatus.ToolTipText = _snapshot.BoardSensor;
                UpdateDiskIcons(_snapshot.Disks);
                UpdateDiskMenu(_snapshot.Disks);
                UpdateMemoryIcons(_snapshot.Memories);
                UpdateMemoryMenu(_snapshot.Memories);
                _gpuStatus.Enabled = _snapshot.Gpu.HasValue;
                _gpuIcon.Visible = _trayReady && _snapshot.Gpu.HasValue &&
                    IsTraySelectionEnabled("gpu", true);
                _dashboard.UpdateValues(_snapshot, _intervalSeconds, _startupEnabled, _isAdministrator, _isPawnIoInstalled);
            }
            else
            {
                SetError(_cpuIcon, "CPU");
                SetError(_gpuIcon, "GPU");
                SetError(_boardIcon, "Placa base");
                foreach (NotifyIcon diskIcon in _diskIcons.Values)
                    SetError(diskIcon, "Disco");
                foreach (NotifyIcon memoryIcon in _memoryIcons.Values)
                    SetError(memoryIcon, "memoria RAM");
                _dashboard.ShowError(error ?? "Error desconocido");
            }
        }

        private void ShowRequestSignaled(object state, bool timedOut)
        {
            if (_closing || timedOut) return;
            try
            {
                _dashboard.BeginInvoke(new MethodInvoker(delegate
                {
                    if (!_closing) ShowDashboard();
                }));
            }
            catch { }
        }

        private void UpdateIcon(NotifyIcon icon, string label, float? value, Color baseColor)
        {
            string number = value.HasValue ? Math.Round(value.Value).ToString("0", CultureInfo.InvariantCulture) : "--";
            Color color = TemperatureColor(value, baseColor);
            string display = value.HasValue ? number + "\u00B0" : "--";
            string signature = IconSignature(display, color);
            string tooltip = Truncate("PcTemp · " + label + ": " + FormatTemperature(value), 63);
            string previous;
            if (_iconSignatures.TryGetValue(icon, out previous) && previous == signature)
            {
                if (icon.Text != tooltip) icon.Text = tooltip;
                return;
            }
            Icon next = TrayIconFactory.Create(display, color);
            Icon old = icon.Icon;
            icon.Icon = next;
            if (old != null) old.Dispose();
            icon.Text = tooltip;
            _iconSignatures[icon] = signature;
        }

        private static string IconSignature(string display, Color color)
        {
            return display + "|" + color.ToArgb().ToString(CultureInfo.InvariantCulture) + "|" +
                TrayIconFactory.PreferredSize.ToString(CultureInfo.InvariantCulture);
        }

        private static Color TemperatureColor(float? value, Color baseColor)
        {
            if (!value.HasValue) return Color.Gray;
            if (value.Value >= 90) return Color.Red;
            if (value.Value >= 80) return Color.OrangeRed;
            if (value.Value >= 70) return Color.Orange;
            return baseColor;
        }

        private void SetError(NotifyIcon icon, string label)
        {
            string signature = IconSignature("!", Color.OrangeRed);
            string previous;
            if (_iconSignatures.TryGetValue(icon, out previous) && previous == signature) return;
            Icon next = TrayIconFactory.Create("!", Color.OrangeRed);
            Icon old = icon.Icon;
            icon.Icon = next;
            if (old != null) old.Dispose();
            icon.Text = Truncate("PcTemp · Error al leer " + label, 63);
            _iconSignatures[icon] = signature;
        }

        private static string FormatTemperature(float? value)
        {
            return value.HasValue ? Math.Round(value.Value).ToString("0", CultureInfo.CurrentCulture) + " °C" : "no detectada";
        }

        private void UpdateSessionStatistics(TemperatureSnapshot snapshot)
        {
            _cpuStats.Update(snapshot.Cpu);
            _gpuStats.Update(snapshot.Gpu);
            _boardStats.Update(snapshot.Board);
            foreach (DiskTemperature disk in snapshot.Disks)
            {
                SessionTemperatureStats stats;
                if (!_diskStats.TryGetValue(disk.Id, out stats))
                {
                    stats = new SessionTemperatureStats();
                    _diskStats.Add(disk.Id, stats);
                }
                stats.Update(disk.Value);
            }
            foreach (MemoryTemperature memory in snapshot.Memories)
            {
                SessionTemperatureStats stats;
                if (!_memoryStats.TryGetValue(memory.Id, out stats))
                {
                    stats = new SessionTemperatureStats();
                    _memoryStats.Add(memory.Id, stats);
                }
                stats.Update(memory.Value);
            }
        }

        private static string FormatSessionTemperature(string label, float? current, SessionTemperatureStats stats)
        {
            return label + ": " + FormatTemperature(current) +
                " · mín. " + FormatTemperature(stats.Minimum) +
                " · máx. " + FormatTemperature(stats.Maximum);
        }

        private void UpdateDiskMenu(List<DiskTemperature> disks)
        {
            if (disks == null || disks.Count == 0)
            {
                foreach (ToolStripMenuItem old in _diskMenuItems.Values) old.Dispose();
                _diskMenuItems.Clear();
                _diskStatus.DropDownItems.Clear();
                _diskStatus.Text = "Discos: sin sensores de temperatura";
                _diskStatus.Enabled = false;
                return;
            }

            while (true)
            {
                string missing = null;
                foreach (string id in _diskMenuItems.Keys)
                {
                    if (!SensorCollection.Contains(disks, id)) { missing = id; break; }
                }
                if (missing == null) break;
                ToolStripMenuItem old = _diskMenuItems[missing];
                _diskStatus.DropDownItems.Remove(old);
                _diskMenuItems.Remove(missing);
                old.Dispose();
            }

            _diskStatus.Text = "Discos (" + disks.Count + ")";
            _diskStatus.Enabled = true;
            foreach (DiskTemperature disk in disks)
            {
                string name = disk.Name ?? "Disco";
                if (name.Length > 32) name = name.Substring(0, 29) + "…";
                SessionTemperatureStats stats;
                if (!_diskStats.TryGetValue(disk.Id, out stats))
                    stats = new SessionTemperatureStats();
                ToolStripMenuItem item;
                if (!_diskMenuItems.TryGetValue(disk.Id, out item))
                {
                    item = new ToolStripMenuItem { CheckOnClick = true, Tag = disk.Id };
                    item.Checked = IsTraySelectionEnabled(disk.Id, false);
                    item.CheckedChanged += SensorMenuCheckedChanged;
                    _diskMenuItems.Add(disk.Id, item);
                    _diskStatus.DropDownItems.Add(item);
                }
                item.Text = FormatSessionTemperature(name, disk.Value, stats);
            }
        }

        private void SensorMenuCheckedChanged(object sender, EventArgs e)
        {
            if (_updatingTraySelectionUi) return;
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            string id = item == null ? null : item.Tag as string;
            if (!string.IsNullOrEmpty(id)) SetTraySelection(id, item.Checked);
        }

        private void UpdateDiskIcons(List<DiskTemperature> disks)
        {
            while (true)
            {
                string missing = null;
                foreach (string id in _diskIcons.Keys)
                {
                    if (!SensorCollection.Contains(disks, id)) { missing = id; break; }
                }
                if (missing == null) break;
                _iconSignatures.Remove(_diskIcons[missing]);
                _diskIcons[missing].Visible = false;
                _diskIcons[missing].Dispose();
                _diskIcons.Remove(missing);
            }

            foreach (DiskTemperature disk in disks)
            {
                bool selected = IsTraySelectionEnabled(disk.Id, false);
                NotifyIcon icon;
                if (!_diskIcons.TryGetValue(disk.Id, out icon) && selected)
                {
                    icon = CreateNotifyIcon(disk.Name, DashboardForm.LoadCardColor(disk.Id, DashboardForm.GetDefaultDiskColor(disk.Id)));
                    _diskIcons.Add(disk.Id, icon);
                }

                if (icon != null)
                {
                    UpdateIcon(icon, disk.Name, disk.Value, DashboardForm.LoadCardColor(disk.Id, DashboardForm.GetDefaultDiskColor(disk.Id)));
                    icon.Visible = _trayReady && selected;
                }
            }
        }

        private void UpdateMemoryMenu(List<MemoryTemperature> memories)
        {
            if (memories == null || memories.Count == 0)
            {
                foreach (ToolStripMenuItem old in _memoryMenuItems.Values) old.Dispose();
                _memoryMenuItems.Clear();
                _memoryStatus.DropDownItems.Clear();
                _memoryStatus.Text = "Memoria RAM: sin sensores de temperatura";
                _memoryStatus.Enabled = false;
                return;
            }

            while (true)
            {
                string missing = null;
                foreach (string id in _memoryMenuItems.Keys)
                {
                    if (!SensorCollection.Contains(memories, id)) { missing = id; break; }
                }
                if (missing == null) break;
                ToolStripMenuItem old = _memoryMenuItems[missing];
                _memoryStatus.DropDownItems.Remove(old);
                _memoryMenuItems.Remove(missing);
                old.Dispose();
            }

            _memoryStatus.Text = "Memoria RAM (" + memories.Count + ")";
            _memoryStatus.Enabled = true;
            foreach (MemoryTemperature memory in memories)
            {
                string name = memory.Name ?? "Módulo RAM";
                if (name.Length > 32) name = name.Substring(0, 29) + "…";
                SessionTemperatureStats stats;
                if (!_memoryStats.TryGetValue(memory.Id, out stats))
                    stats = new SessionTemperatureStats();
                ToolStripMenuItem item;
                if (!_memoryMenuItems.TryGetValue(memory.Id, out item))
                {
                    item = new ToolStripMenuItem { CheckOnClick = true, Tag = memory.Id };
                    item.Checked = IsTraySelectionEnabled(memory.Id, false);
                    item.CheckedChanged += SensorMenuCheckedChanged;
                    _memoryMenuItems.Add(memory.Id, item);
                    _memoryStatus.DropDownItems.Add(item);
                }
                item.Text = FormatSessionTemperature(name, memory.Value, stats);
            }
        }

        private void UpdateMemoryIcons(List<MemoryTemperature> memories)
        {
            while (true)
            {
                string missing = null;
                foreach (string id in _memoryIcons.Keys)
                {
                    if (!SensorCollection.Contains(memories, id)) { missing = id; break; }
                }
                if (missing == null) break;
                _iconSignatures.Remove(_memoryIcons[missing]);
                _memoryIcons[missing].Visible = false;
                _memoryIcons[missing].Dispose();
                _memoryIcons.Remove(missing);
            }

            foreach (MemoryTemperature memory in memories)
            {
                bool selected = IsTraySelectionEnabled(memory.Id, false);
                NotifyIcon icon;
                if (!_memoryIcons.TryGetValue(memory.Id, out icon) && selected)
                {
                    icon = CreateNotifyIcon(memory.Name, DashboardForm.LoadCardColor(memory.Id, DashboardForm.GetDefaultMemoryColor(memory.Id)));
                    _memoryIcons.Add(memory.Id, icon);
                }

                if (icon != null)
                {
                    UpdateIcon(icon, memory.Name, memory.Value, DashboardForm.LoadCardColor(memory.Id, DashboardForm.GetDefaultMemoryColor(memory.Id)));
                    icon.Visible = _trayReady && selected;
                }
            }
        }

        private bool IsTraySelectionEnabled(string id)
        {
            return IsTraySelectionEnabled(id, id == "cpu" || id == "gpu" || id == "board");
        }

        private bool IsTraySelectionEnabled(string id, bool defaultValue)
        {
            bool cached;
            if (_traySelectionCache.TryGetValue(id, out cached)) return cached;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                object value = key == null ? null : key.GetValue(TraySelectionValueName(id));
                bool enabled = value == null ? defaultValue : Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                _traySelectionCache[id] = enabled;
                return enabled;
            }
        }

        private void SetTraySelection(string id, bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                key.SetValue(TraySelectionValueName(id), enabled ? 1 : 0, RegistryValueKind.DWord);
            _traySelectionCache[id] = enabled;
            SynchronizeTraySelectionControls(id, enabled);

            if (id == "cpu") _cpuIcon.Visible = _trayReady && enabled;
            else if (id == "gpu") _gpuIcon.Visible = _trayReady && enabled && _snapshot.Gpu.HasValue;
            else if (id == "board") _boardIcon.Visible = _trayReady && enabled;
            else
            {
                MemoryTemperature memory = _snapshot.Memories.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (memory != null)
                {
                    NotifyIcon memoryIcon;
                    if (!_memoryIcons.TryGetValue(id, out memoryIcon) && enabled)
                    {
                        memoryIcon = CreateNotifyIcon(memory.Name, DashboardForm.LoadCardColor(id, DashboardForm.GetDefaultMemoryColor(id)));
                        _memoryIcons.Add(id, memoryIcon);
                        UpdateIcon(memoryIcon, memory.Name, memory.Value, DashboardForm.LoadCardColor(id, DashboardForm.GetDefaultMemoryColor(id)));
                    }
                    if (_memoryIcons.TryGetValue(id, out memoryIcon)) memoryIcon.Visible = _trayReady && enabled;
                    return;
                }

                DiskTemperature disk = _snapshot.Disks.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                NotifyIcon icon;
                if (disk != null && !_diskIcons.TryGetValue(id, out icon) && enabled)
                {
                    icon = CreateNotifyIcon(disk.Name, DashboardForm.LoadCardColor(id, DashboardForm.GetDefaultDiskColor(id)));
                    _diskIcons.Add(id, icon);
                    UpdateIcon(icon, disk.Name, disk.Value, DashboardForm.LoadCardColor(id, DashboardForm.GetDefaultDiskColor(id)));
                }
                if (_diskIcons.TryGetValue(id, out icon)) icon.Visible = _trayReady && enabled;
            }
        }

        private void SynchronizeTraySelectionControls(string id, bool enabled)
        {
            ToolStripMenuItem item = null;
            if (string.Equals(id, "cpu", StringComparison.OrdinalIgnoreCase)) item = _cpuStatus;
            else if (string.Equals(id, "gpu", StringComparison.OrdinalIgnoreCase)) item = _gpuStatus;
            else if (string.Equals(id, "board", StringComparison.OrdinalIgnoreCase)) item = _boardStatus;
            else if (!_diskMenuItems.TryGetValue(id, out item)) _memoryMenuItems.TryGetValue(id, out item);

            if (item != null && item.Checked != enabled)
            {
                _updatingTraySelectionUi = true;
                try { item.Checked = enabled; }
                finally { _updatingTraySelectionUi = false; }
            }
            _dashboard.SetTraySelectionState(id, enabled);
        }

        private void ApplyMainTrayVisibility()
        {
            _cpuIcon.Visible = IsTraySelectionEnabled("cpu", true);
            _gpuIcon.Visible = _snapshot.Gpu.HasValue && IsTraySelectionEnabled("gpu", true);
            _boardIcon.Visible = IsTraySelectionEnabled("board", true);
        }

        private static string TraySelectionValueName(string id)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(id ?? ""))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "Tray_" + encoded;
        }

        private static string Truncate(string value, int length)
        {
            return value.Length <= length ? value : value.Substring(0, length);
        }

        private void SetInterval(int seconds)
        {
            _intervalSeconds = seconds;
            _timer.Interval = seconds * 1000;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                key.SetValue("IntervalSeconds", seconds, RegistryValueKind.DWord);
            UpdateIntervalChecks();
            _dashboard.UpdateValues(_snapshot, _intervalSeconds, _startupEnabled, _isAdministrator, _isPawnIoInstalled);
        }

        private void UpdateIntervalChecks()
        {
            foreach (ToolStripMenuItem item in _intervalMenu.DropDownItems.OfType<ToolStripMenuItem>())
                item.Checked = Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture) == _intervalSeconds;
        }

        private static int LoadInterval()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                object value = key == null ? null : key.GetValue("IntervalSeconds");
                int seconds;
                if (value != null && int.TryParse(value.ToString(), out seconds) &&
                    new[] { 2, 5, 10, 30, 60 }.Contains(seconds))
                    return seconds;
            }
            return 5;
        }

        private static string LoadBoardSensorSelection()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                    return Convert.ToString(key == null ? null : key.GetValue("BoardSensorSelection")) ?? "";
            }
            catch { return ""; }
        }

        private void BoardSensorChanged(string sensorId)
        {
            _boardSensorSelection = sensorId ?? "";
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                key.SetValue("BoardSensorSelection", _boardSensorSelection, RegistryValueKind.String);
            BeginRefreshTemperatures();
        }

        private void EnsureFirstRunStartup()
        {
            using (RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                if (settings.GetValue("Initialized") == null)
                {
                    SetStartup(true);
                    settings.SetValue("Initialized", 1, RegistryValueKind.DWord);
                }
            }
        }

        private void StartupClicked(object sender, EventArgs e)
        {
            try
            {
                SetStartup(_startupItem.Checked);
                _startupEnabled = IsStartupEnabled();
                _startupItem.Checked = _startupEnabled;
                _dashboard.UpdateValues(_snapshot, _intervalSeconds, _startupEnabled, _isAdministrator, _isPawnIoInstalled);
            }
            catch (Exception ex)
            {
                _startupItem.Checked = !_startupItem.Checked;
                MessageBox.Show("No se pudo cambiar el inicio con Windows:\n" + ex.Message,
                    "PcTemp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void DashboardStartupChanged(bool enabled)
        {
            try
            {
                SetStartup(enabled);
                _startupEnabled = IsStartupEnabled();
                _startupItem.Checked = _startupEnabled;
                _dashboard.UpdateValues(_snapshot, _intervalSeconds, _startupEnabled,
                    _isAdministrator, _isPawnIoInstalled);
            }
            catch (Exception ex)
            {
                _startupEnabled = IsStartupEnabled();
                _startupItem.Checked = _startupEnabled;
                MessageBox.Show("No se pudo cambiar el inicio con Windows:\n" + ex.Message,
                    "PcTemp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _dashboard.UpdateValues(_snapshot, _intervalSeconds, _startupEnabled,
                    _isAdministrator, _isPawnIoInstalled);
            }
        }

        private void CardColorChanged(string id, Color color)
        {
            if (id == "cpu") UpdateIcon(_cpuIcon, "CPU", _snapshot.Cpu, color);
            else if (id == "gpu") UpdateIcon(_gpuIcon, "GPU", _snapshot.Gpu, color);
            else if (id == "board") UpdateIcon(_boardIcon, "Placa base", _snapshot.Board, color);
            else
            {
                MemoryTemperature memory = _snapshot.Memories.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                NotifyIcon memoryIcon;
                if (memory != null && _memoryIcons.TryGetValue(id, out memoryIcon))
                {
                    UpdateIcon(memoryIcon, memory.Name, memory.Value, color);
                    return;
                }

                DiskTemperature disk = _snapshot.Disks.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                NotifyIcon icon;
                if (disk != null && _diskIcons.TryGetValue(id, out icon))
                    UpdateIcon(icon, disk.Name, disk.Value, color);
            }
        }

        private static bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                if (key != null && key.GetValue(RunValue) != null)
                    return true;
            }
            return ScheduledStartup.Exists();
        }

        private static void SetStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                {
                    if (IsAdministrator())
                    {
                        try
                        {
                            ScheduledStartup.Create(Application.ExecutablePath);
                            key.DeleteValue(RunValue, false);
                            return;
                        }
                        catch { }
                    }
                    key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --startup");
                }
                else
                {
                    key.DeleteValue(RunValue, false);
                    try { ScheduledStartup.Delete(); } catch { }
                }
            }
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void UpgradeStartupRegistration()
        {
            if (!_isAdministrator) return;
            if (IsStartupEnabled())
                SetStartup(true);
        }

        private void RequestElevation()
        {
            if (_isAdministrator) return;
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(Application.ExecutablePath, "--elevated");
                start.UseShellExecute = true;
                start.Verb = "runas";
                Process.Start(start);
                CloseApplication();
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223)
                    MessageBox.Show("No se pudo reiniciar PcTemp como administrador:\n" + ex.Message,
                        "PcTemp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InstallSensorDriver()
        {
            string installer = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PawnIO_setup.exe");
            if (!File.Exists(installer))
            {
                MessageBox.Show("No se encuentra PawnIO_setup.exe junto a PcTemp.", "PcTemp",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult answer = MessageBox.Show(
                "PcTemp necesita el controlador firmado PawnIO para leer los sensores de bajo nivel de tu CPU y placa base.\n\n" +
                "Se abrirá el instalador oficial 2.2.0 y Windows pedirá permiso de administrador. ¿Continuar?",
                "Instalar acceso a sensores", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return;

            try
            {
                ProcessStartInfo start = new ProcessStartInfo(installer);
                start.UseShellExecute = true;
                start.Verb = "runas";
                Process.Start(start);
                CloseApplication();
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223)
                    MessageBox.Show("No se pudo abrir el instalador de PawnIO:\n" + ex.Message,
                        "PcTemp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void OpenTraySettings()
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo("ms-settings:taskbar");
                start.UseShellExecute = true;
                Process.Start(start);
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo abrir la configuración de la barra de tareas:\n" + ex.Message,
                    "PcTemp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void MigrateLegacySettings()
        {
            using (RegistryKey current = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                if (current != null)
                {
                    using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
                        run.DeleteValue(LegacyRunValue, false);
                    return;
                }
            }

            object oldInterval = null;
            using (RegistryKey legacy = Registry.CurrentUser.OpenSubKey(LegacySettingsKey))
            {
                if (legacy != null)
                {
                    oldInterval = legacy.GetValue("IntervalSeconds");
                }
            }

            using (RegistryKey current = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                if (oldInterval != null) current.SetValue("IntervalSeconds", oldInterval, RegistryValueKind.DWord);
            }

            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
                run.DeleteValue(LegacyRunValue, false);
        }

        private void ShowDashboard()
        {
            _dashboard.UpdateValues(_snapshot, _intervalSeconds, _startupEnabled, _isAdministrator, _isPawnIoInstalled);
            _dashboard.WindowState = FormWindowState.Normal;
            _dashboard.OptimizeWindowSize();
            _dashboard.Show();
            _dashboard.Activate();
        }

        private void DashboardClosing(object sender, FormClosingEventArgs e)
        {
            if (!_closing)
            {
                e.Cancel = true;
                _dashboard.Hide();
            }
        }

        private void CloseApplication()
        {
            _closing = true;
            Application.Idle -= FirstApplicationIdle;
            _showRequestRegistration.Unregister(null);
            _timer.Stop();
            _cpuIcon.Visible = false;
            _gpuIcon.Visible = false;
            _boardIcon.Visible = false;
            foreach (NotifyIcon diskIcon in _diskIcons.Values)
                diskIcon.Visible = false;
            foreach (NotifyIcon memoryIcon in _memoryIcons.Values)
                memoryIcon.Visible = false;
            _dashboard.Close();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _showRequestRegistration.Unregister(null);
                _timer.Dispose();
                _cpuIcon.Dispose();
                _gpuIcon.Dispose();
                _boardIcon.Dispose();
                foreach (NotifyIcon diskIcon in _diskIcons.Values)
                    diskIcon.Dispose();
                _diskIcons.Clear();
                foreach (NotifyIcon memoryIcon in _memoryIcons.Values)
                    memoryIcon.Dispose();
                _memoryIcons.Clear();
                _menu.Dispose();
                if (Interlocked.CompareExchange(ref _reading, 0, 0) == 0)
                    DisposeSensorReader();
                _dashboard.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class SensorAccess
    {
        public static bool IsPawnIoInstalled()
        {
            string driver = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "PawnIO.sys");
            if (File.Exists(driver)) return true;

            try
            {
                using (RegistryKey service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"))
                {
                    if (service != null && Convert.ToInt32(service.GetValue("Start", 4), CultureInfo.InvariantCulture) != 4)
                    {
                        string imagePath = Convert.ToString(service.GetValue("ImagePath"), CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace(imagePath))
                        {
                            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                            if (imagePath.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                                imagePath = Path.Combine(windows, imagePath.Substring(@"\SystemRoot\".Length));
                            else if (imagePath.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
                                imagePath = imagePath.Substring(4);
                            else
                                imagePath = Environment.ExpandEnvironmentVariables(imagePath);
                            if (File.Exists(imagePath)) return true;
                        }
                    }
                }
            }
            catch { }

            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey uninstall = machine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        foreach (string name in uninstall.GetSubKeyNames())
                        {
                            using (RegistryKey item = uninstall.OpenSubKey(name))
                            {
                                string display = Convert.ToString(item == null ? null : item.GetValue("DisplayName"));
                                if (display.IndexOf("PawnIO", StringComparison.OrdinalIgnoreCase) >= 0)
                                    return true;
                            }
                        }
                    }
                }
                catch { }
            }
            return false;
        }
    }

    internal static class ScheduledStartup
    {
        private const string TaskName = "PcTemp";

        public static bool Exists()
        {
            try
            {
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                dynamic task = folder.GetTask(TaskName);
                return task != null;
            }
            catch { return false; }
        }

        public static void Create(string executablePath)
        {
            string user = WindowsIdentity.GetCurrent().Name;
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = "Muestra las temperaturas de CPU, GPU y placa base en la bandeja.";
            definition.Principal.UserId = user;
            definition.Principal.LogonType = 3;
            definition.Principal.RunLevel = 1;
            definition.Settings.Enabled = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.MultipleInstances = 2;

            dynamic trigger = definition.Triggers.Create(9);
            trigger.Enabled = true;
            trigger.UserId = user;
            dynamic action = definition.Actions.Create(0);
            action.Path = executablePath;
            action.Arguments = "--startup";
            action.WorkingDirectory = Path.GetDirectoryName(executablePath);

            folder.RegisterTaskDefinition(TaskName, definition, 6, user, null, 3, null);
        }

        public static void Delete()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            try { folder.DeleteTask(TaskName, 0); } catch { }
        }
    }

    internal sealed class ToggleSwitch : CheckBox
    {
        private Color _accentColor = Color.DodgerBlue;
        public Color AccentColor
        {
            get { return _accentColor; }
            set { _accentColor = value; Invalidate(); }
        }

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
            Text = "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? Color.Transparent : Parent.BackColor);
            Rectangle track = new Rectangle(0, 2, Width - 1, Height - 5);
            int radius = track.Height;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(track.Left, track.Top, radius, radius, 90, 180);
                path.AddArc(track.Right - radius, track.Top, radius, radius, 270, 180);
                path.CloseFigure();
                using (Brush brush = new SolidBrush(Checked ? AccentColor : Color.FromArgb(105, 110, 122)))
                    e.Graphics.FillPath(brush, path);
                double luminance = (0.2126 * AccentColor.R + 0.7152 * AccentColor.G + 0.0722 * AccentColor.B) / 255.0;
                int knob = track.Height - 4;
                int x = Checked ? track.Right - knob - 2 : track.Left + 2;
                Color knobColor = Checked && luminance > 0.78 ? Color.FromArgb(55, 58, 65) : Color.White;
                using (Brush knobBrush = new SolidBrush(knobColor))
                    e.Graphics.FillEllipse(knobBrush, x, track.Top + 2, knob, knob);
            }
        }
    }

    internal sealed class ThemedVScrollBar : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _largeChange = 10;
        private int _smallChange = 10;
        private int _value;
        private bool _dragging;
        private bool _hoveringThumb;
        private int _dragOffset;
        private Color _thumbColor = Color.FromArgb(118, 122, 130);

        public event EventHandler ValueChanged;

        public int Minimum
        {
            get { return _minimum; }
            set { _minimum = value; if (_maximum < value) _maximum = value; Value = _value; Invalidate(); }
        }

        public int Maximum
        {
            get { return _maximum; }
            set { _maximum = Math.Max(_minimum, value); Value = _value; Invalidate(); }
        }

        public int LargeChange
        {
            get { return _largeChange; }
            set { _largeChange = Math.Max(1, value); Value = _value; Invalidate(); }
        }

        public int SmallChange
        {
            get { return _smallChange; }
            set { _smallChange = Math.Max(1, value); }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int clamped = Math.Max(_minimum, Math.Min(GetMaximumValue(), value));
                if (_value == clamped) return;
                _value = clamped;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public Color ThumbColor
        {
            get { return _thumbColor; }
            set { _thumbColor = value; Invalidate(); }
        }

        public ThemedVScrollBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle thumb = GetThumbRectangle();
            Color color = _hoveringThumb || _dragging ? ControlPaint.Light(ThumbColor, 0.18F) : ThumbColor;
            using (GraphicsPath path = CreateRoundedPath(thumb, thumb.Width / 2F))
            using (SolidBrush brush = new SolidBrush(color))
                e.Graphics.FillPath(brush, path);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Rectangle thumb = GetThumbRectangle();
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragOffset = e.Y - thumb.Top;
                Capture = true;
            }
            else
            {
                Value += e.Y < thumb.Top ? -LargeChange : LargeChange;
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle thumb = GetThumbRectangle();
            bool hovering = thumb.Contains(e.Location);
            if (_hoveringThumb != hovering)
            {
                _hoveringThumb = hovering;
                Invalidate();
            }
            if (!_dragging) return;
            int trackTop = 6;
            int available = Math.Max(1, Height - 12 - thumb.Height);
            int top = Math.Max(trackTop, Math.Min(trackTop + available, e.Y - _dragOffset));
            int range = Math.Max(0, GetMaximumValue() - Minimum);
            Value = Minimum + (int)Math.Round((top - trackTop) * range / (double)available);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoveringThumb = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int notches = e.Delta / System.Windows.Forms.SystemInformation.MouseWheelScrollDelta;
            if (notches == 0) notches = Math.Sign(e.Delta);
            Value -= notches * SmallChange;
        }

        private int GetMaximumValue()
        {
            return Math.Max(Minimum, Maximum - LargeChange + 1);
        }

        private Rectangle GetThumbRectangle()
        {
            int trackHeight = Math.Max(1, Height - 12);
            int total = Math.Max(1, Maximum - Minimum + 1);
            int thumbHeight = Math.Max(36, (int)Math.Round(trackHeight * Math.Min(1.0, LargeChange / (double)total)));
            thumbHeight = Math.Min(trackHeight, thumbHeight);
            int available = Math.Max(0, trackHeight - thumbHeight);
            int range = Math.Max(0, GetMaximumValue() - Minimum);
            int y = 6 + (range == 0 ? 0 : (int)Math.Round((_value - Minimum) * available / (double)range));
            const int thumbWidth = 5;
            return new Rectangle(Math.Max(0, (Width - thumbWidth) / 2), y, thumbWidth, thumbHeight);
        }

        private static GraphicsPath CreateRoundedPath(Rectangle bounds, float radius)
        {
            float diameter = Math.Max(1F, Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2F));
            RectangleF rectangle = bounds;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180F, 90F);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270F, 90F);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0F, 90F);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90F, 90F);
            path.CloseFigure();
            return path;
        }
    }

    internal enum HardwareIconKind
    {
        Cpu,
        Gpu,
        Board,
        Disk,
        Memory
    }

    internal sealed class HardwareIcon : Control
    {
        public HardwareIconKind Kind { get; set; }

        public HardwareIcon()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnForeColorChanged(EventArgs e)
        {
            base.OnForeColorChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = Math.Max(0.1F, Math.Min(Width, Height) / 24F);
            float offsetX = (Width - 24F * scale) / 2F;
            float offsetY = (Height - 24F * scale) / 2F;
            GraphicsState state = e.Graphics.Save();
            e.Graphics.TranslateTransform(offsetX, offsetY);
            e.Graphics.ScaleTransform(scale, scale);
            using (Pen pen = new Pen(ForeColor, 1.55F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                if (Kind == HardwareIconKind.Cpu) DrawCpu(e.Graphics, pen);
                else if (Kind == HardwareIconKind.Gpu) DrawGpu(e.Graphics, pen);
                else if (Kind == HardwareIconKind.Board) DrawBoard(e.Graphics, pen);
                else if (Kind == HardwareIconKind.Memory) DrawMemory(e.Graphics, pen);
                else DrawDisk(e.Graphics, pen);
            }
            e.Graphics.Restore(state);
        }

        private static void DrawPins(Graphics graphics, Pen pen, RectangleF body)
        {
            for (int index = 1; index <= 3; index++)
            {
                float x = body.Left + body.Width * index / 4F;
                float y = body.Top + body.Height * index / 4F;
                graphics.DrawLine(pen, x, 2.5F, x, body.Top);
                graphics.DrawLine(pen, x, body.Bottom, x, 21.5F);
                graphics.DrawLine(pen, 2.5F, y, body.Left, y);
                graphics.DrawLine(pen, body.Right, y, 21.5F, y);
            }
        }

        private static void DrawCpu(Graphics graphics, Pen pen)
        {
            RectangleF body = new RectangleF(6F, 6F, 12F, 12F);
            DrawPins(graphics, pen, body);
            using (GraphicsPath package = new GraphicsPath())
            {
                package.AddPolygon(new[]
                {
                    new PointF(8F, 6F), new PointF(16F, 6F), new PointF(18F, 8F),
                    new PointF(18F, 16F), new PointF(16F, 18F), new PointF(8F, 18F),
                    new PointF(6F, 16F), new PointF(6F, 8F)
                });
                package.CloseFigure();
                graphics.DrawPath(pen, package);
            }
            DrawRoundedRectangle(graphics, pen, new RectangleF(9F, 9F, 6F, 6F), 0.8F);
        }

        private static void DrawGpu(Graphics graphics, Pen pen)
        {
            RectangleF body = new RectangleF(5.5F, 5.5F, 13F, 13F);
            DrawPins(graphics, pen, body);
            DrawRoundedRectangle(graphics, pen, body, 1.2F);
            DrawRoundedRectangle(graphics, pen, new RectangleF(8.5F, 8.5F, 7F, 7F), 0.7F);
            graphics.DrawEllipse(pen, 6.9F, 7F, 0.8F, 0.8F);
            graphics.DrawEllipse(pen, 6.9F, 9.5F, 0.8F, 0.8F);
            graphics.DrawEllipse(pen, 6.9F, 12F, 0.8F, 0.8F);
        }

        private static void DrawBoard(Graphics graphics, Pen pen)
        {
            DrawRoundedRectangle(graphics, pen, new RectangleF(2.5F, 2.5F, 19F, 19F), 1.5F);
            DrawRoundedRectangle(graphics, pen, new RectangleF(5F, 5F, 9F, 9F), 0.8F);
            graphics.DrawRectangle(pen, 7F, 7F, 5F, 5F);
            graphics.DrawLine(pen, 16.5F, 4.5F, 16.5F, 14.5F);
            graphics.DrawLine(pen, 19F, 4.5F, 19F, 14.5F);
            graphics.DrawLine(pen, 5F, 17F, 14F, 17F);
            graphics.DrawLine(pen, 5F, 19.5F, 11F, 19.5F);
            graphics.DrawLine(pen, 14F, 17F, 14F, 20F);
        }

        private static void DrawDisk(Graphics graphics, Pen pen)
        {
            DrawRoundedRectangle(graphics, pen, new RectangleF(3F, 3F, 18F, 18F), 2F);
            graphics.DrawEllipse(pen, 6F, 6F, 10F, 10F);
            graphics.DrawEllipse(pen, 9.5F, 9.5F, 3F, 3F);
            graphics.DrawLine(pen, 11.5F, 11.5F, 17.5F, 17.5F);
            graphics.DrawEllipse(pen, 17F, 17F, 1.5F, 1.5F);
        }

        private static void DrawMemory(Graphics graphics, Pen pen)
        {
            RectangleF module = new RectangleF(2F, 6F, 20F, 11F);
            DrawRoundedRectangle(graphics, pen, module, 1F);
            for (int index = 0; index < 4; index++)
            {
                float chipX = 4F + index * 4.4F;
                DrawRoundedRectangle(graphics, pen, new RectangleF(chipX, 8F, 3F, 6F), 0.4F);
            }
            for (int index = 0; index < 6; index++)
            {
                float x = 4F + index * 3.2F;
                graphics.DrawLine(pen, x, 17F, x, 20F);
            }
            graphics.DrawLine(pen, 10F, 17F, 12F, 15F);
            graphics.DrawLine(pen, 12F, 15F, 14F, 17F);
        }

        private static void DrawRoundedRectangle(Graphics graphics, Pen pen, RectangleF bounds, float radius)
        {
            float diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2F);
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180F, 90F);
                path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270F, 90F);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0F, 90F);
                path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90F, 90F);
                path.CloseFigure();
                graphics.DrawPath(pen, path);
            }
        }
    }

    internal sealed class PcTempMenuColorTable : ProfessionalColorTable
    {
        private readonly Color _background;
        private readonly Color _selected;
        private readonly Color _border;

        public PcTempMenuColorTable(bool dark)
        {
            UseSystemColors = false;
            _background = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(229, 233, 239);
            _selected = dark ? Color.FromArgb(45, 48, 56) : Color.FromArgb(224, 235, 250);
            _border = dark ? Color.FromArgb(62, 66, 76) : Color.FromArgb(185, 195, 210);
        }

        public override Color ToolStripDropDownBackground { get { return _background; } }
        public override Color ImageMarginGradientBegin { get { return _background; } }
        public override Color ImageMarginGradientMiddle { get { return _background; } }
        public override Color ImageMarginGradientEnd { get { return _background; } }
        public override Color MenuBorder { get { return _border; } }
        public override Color MenuItemBorder { get { return _border; } }
        public override Color MenuItemSelected { get { return _selected; } }
        public override Color MenuItemSelectedGradientBegin { get { return _selected; } }
        public override Color MenuItemSelectedGradientEnd { get { return _selected; } }
        public override Color MenuItemPressedGradientBegin { get { return _selected; } }
        public override Color MenuItemPressedGradientEnd { get { return _selected; } }
        public override Color ToolStripGradientBegin { get { return _background; } }
        public override Color ToolStripGradientMiddle { get { return _background; } }
        public override Color ToolStripGradientEnd { get { return _background; } }
        public override Color CheckBackground { get { return _selected; } }
        public override Color CheckSelectedBackground { get { return _selected; } }
        public override Color CheckPressedBackground { get { return _selected; } }
        public override Color SeparatorDark { get { return _border; } }
        public override Color SeparatorLight { get { return _background; } }
    }

    internal sealed class PcTempMenuRenderer : ToolStripProfessionalRenderer
    {
        public PcTempMenuRenderer(bool dark)
            : base(new PcTempMenuColorTable(dark))
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is MenuStrip)
            {
                // El menu principal se integra visualmente con la barra de titulo.
                // No se dibuja ningun borde: Windows ya delimita el contenido.
                return;
            }
            base.OnRenderToolStripBorder(e);
        }
    }

    internal sealed class AboutForm : Form
    {
        private readonly Image _appImage;
        private readonly bool _darkTheme;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public AboutForm(Form owner, bool darkTheme)
        {
            _darkTheme = darkTheme;
            Color window = darkTheme ? Color.FromArgb(27, 28, 32) : Color.FromArgb(247, 248, 250);
            Color primary = darkTheme ? Color.FromArgb(245, 246, 248) : Color.FromArgb(25, 28, 34);
            Color secondary = darkTheme ? Color.FromArgb(160, 165, 177) : Color.FromArgb(91, 98, 112);
            Color accent = Color.FromArgb(43, 139, 255);

            Text = "Acerca de PcTemp";
            ClientSize = new Size(440, 452);
            MinimumSize = MaximumSize = SizeFromClientSize(ClientSize);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = window;
            ForeColor = primary;
            Font = new Font("Segoe UI", 9F);
            if (owner != null && owner.Icon != null)
                Icon = owner.Icon;
            try
            {
                string logoPath = Path.Combine(Path.GetDirectoryName(typeof(AboutForm).Assembly.Location), "PcTemp.png");
                if (File.Exists(logoPath))
                {
                    using (Image source = Image.FromFile(logoPath))
                        _appImage = new Bitmap(source);
                }
            }
            catch { }
            if (_appImage == null && owner != null && owner.Icon != null)
                _appImage = owner.Icon.ToBitmap();

            PictureBox logo = new PictureBox
            {
                Location = new Point(168, 28),
                Size = new Size(104, 104),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = _appImage
            };
            Label name = new Label
            {
                Text = "PcTemp " + Program.AppVersion,
                Font = new Font("Segoe UI Semibold", 16F),
                ForeColor = primary,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(24, 145),
                Size = new Size(392, 34)
            };
            Label description = new Label
            {
                Text = "Monitor ligero de temperaturas para Windows",
                Font = new Font("Segoe UI", 10F),
                ForeColor = primary,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(24, 181),
                Size = new Size(392, 26)
            };
            Label components = new Label
            {
                Text = "CPU · GPU · Placa base · Discos · RAM compatible",
                ForeColor = secondary,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(24, 210),
                Size = new Size(392, 24)
            };
            Label details = new Label
            {
                Text = "Motor de sensores: LibreHardwareMonitor\r\nAcceso de bajo nivel: PawnIO 2.2.0\r\n© 2026 PcTemp",
                ForeColor = secondary,
                TextAlign = ContentAlignment.TopCenter,
                Location = new Point(24, 248),
                Size = new Size(392, 64)
            };
            LinkLabel contact = new LinkLabel
            {
                Text = "Contacto: " + Program.ContactEmail,
                LinkColor = accent,
                ActiveLinkColor = Color.DeepSkyBlue,
                VisitedLinkColor = accent,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(70, 310),
                Size = new Size(300, 22),
                TabStop = true
            };
            contact.LinkClicked += delegate { OpenExternal("mailto:" + Program.ContactEmail); };
            LinkLabel github = new LinkLabel
            {
                Text = "GitHub del proyecto",
                LinkColor = accent,
                ActiveLinkColor = Color.DeepSkyBlue,
                VisitedLinkColor = accent,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(120, 334),
                Size = new Size(200, 22),
                TabStop = true
            };
            github.LinkClicked += delegate { OpenExternal(Program.ProjectUrl); };
            LinkLabel licenses = new LinkLabel
            {
                Text = "Ver licencias de terceros",
                LinkColor = accent,
                ActiveLinkColor = Color.DeepSkyBlue,
                VisitedLinkColor = accent,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(120, 358),
                Size = new Size(200, 24),
                TabStop = true
            };
            licenses.LinkClicked += delegate
            {
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "THIRD_PARTY_NOTICES.txt");
                    if (File.Exists(path))
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch { }
            };
            Button close = new Button
            {
                Text = "Cerrar",
                DialogResult = DialogResult.OK,
                BackColor = accent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(160, 400),
                Size = new Size(120, 34),
                TabStop = true
            };
            close.FlatAppearance.BorderSize = 0;
            AcceptButton = close;
            CancelButton = close;
            Controls.AddRange(new Control[] { logo, name, description, components, details, contact, github, licenses, close });
        }

        private static void OpenExternal(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = _darkTheme ? 1 : 0;
                if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
                int caption = ToColorRef(_darkTheme ? Color.FromArgb(27, 28, 32) : Color.White);
                int captionText = ToColorRef(_darkTheme ? Color.White : Color.Black);
                DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(Handle, 36, ref captionText, sizeof(int));
            }
            catch { }
        }

        private static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _appImage != null) _appImage.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class CardDragGhost : Form
    {
        public CardDragGhost(Bitmap preview)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Opacity = 0.78;
            ClientSize = preview.Size;
            BackgroundImage = preview;
            BackgroundImageLayout = ImageLayout.None;
            Cursor = Cursors.SizeAll;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080;
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen border = new Pen(Color.DodgerBlue, 2F))
                e.Graphics.DrawRectangle(border, 1, 1, Math.Max(1, ClientSize.Width - 3), Math.Max(1, ClientSize.Height - 3));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && BackgroundImage != null)
            {
                Image image = BackgroundImage;
                BackgroundImage = null;
                image.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class DigitalFontProvider
    {
        private static readonly object Sync = new object();
        private static readonly PrivateFontCollection Fonts = new PrivateFontCollection();
        private static FontFamily _family;
        private static bool _initialized;

        public static Font Create(float size)
        {
            EnsureLoaded();
            try
            {
                if (_family != null)
                    return new Font(_family, size, FontStyle.Bold, GraphicsUnit.Point);
            }
            catch { }
            return new Font("Bahnschrift SemiCondensed", size, FontStyle.Regular, GraphicsUnit.Point);
        }

        private static void EnsureLoaded()
        {
            if (_initialized) return;
            lock (Sync)
            {
                if (_initialized) return;
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DSEG14Classic-Bold.ttf");
                    if (!File.Exists(path))
                    {
                        string assemblyDirectory = Path.GetDirectoryName(typeof(DigitalFontProvider).Assembly.Location);
                        path = Path.Combine(assemblyDirectory ?? "", "DSEG14Classic-Bold.ttf");
                    }
                    if (File.Exists(path))
                    {
                        Fonts.AddFontFile(path);
                        _family = Fonts.Families.FirstOrDefault(family =>
                            family.Name.IndexOf("DSEG14", StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                }
                catch { _family = null; }
                _initialized = true;
            }
        }
    }

    internal sealed class DigitalValueLabel : Label
    {
        public DigitalValueLabel()
        {
            AutoSize = false;
            BackColor = Color.Transparent;
            UseCompatibleTextRendering = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            UpdateMeasuredSize();
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateMeasuredSize();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (SolidBrush brush = new SolidBrush(ForeColor))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.NoClip;
                e.Graphics.DrawString(Text ?? "", Font, brush, new PointF(0F, 0F), format);
            }
        }

        private void UpdateMeasuredSize()
        {
            if (Font == null) return;
            string text = string.IsNullOrEmpty(Text) ? "--" : Text;
            using (Bitmap bitmap = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                SizeF measured = graphics.MeasureString(text, Font, PointF.Empty, format);
                Size = new Size(Math.Max(1, (int)Math.Ceiling(measured.Width) + 2),
                    Math.Max(1, (int)Math.Ceiling(measured.Height) + 2));
            }
        }
    }

    internal sealed class WindowResizeGrip : Control
    {
        private readonly Form _form;
        private readonly int _hit;
        private readonly Action _dragCompleted;
        private bool _dragging;
        private Point _dragStart;
        private Rectangle _startBounds;

        public WindowResizeGrip(Form form, int hit, Cursor cursor, Action dragCompleted)
        {
            _form = form;
            _hit = hit;
            _dragCompleted = dragCompleted;
            Cursor = cursor;
            TabStop = false;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || _form == null || !_form.IsHandleCreated ||
                _form.WindowState != FormWindowState.Normal)
                return;
            _dragging = true;
            _dragStart = Cursor.Position;
            _startBounds = _form.Bounds;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging || _form == null) return;

            Point current = Cursor.Position;
            int dx = current.X - _dragStart.X;
            int dy = current.Y - _dragStart.Y;
            Rectangle bounds = _startBounds;
            int minimumWidth = Math.Max(1, _form.MinimumSize.Width);
            int minimumHeight = Math.Max(1, _form.MinimumSize.Height);

            bool left = _hit == 10 || _hit == 13 || _hit == 16;
            bool right = _hit == 11 || _hit == 14 || _hit == 17;
            bool top = _hit == 12 || _hit == 13 || _hit == 14;
            bool bottom = _hit == 15 || _hit == 16 || _hit == 17;

            if (left)
            {
                bounds.X = _startBounds.X + dx;
                bounds.Width = _startBounds.Width - dx;
                if (bounds.Width < minimumWidth)
                {
                    bounds.X = _startBounds.Right - minimumWidth;
                    bounds.Width = minimumWidth;
                }
            }
            else if (right)
                bounds.Width = Math.Max(minimumWidth, _startBounds.Width + dx);

            if (top)
            {
                bounds.Y = _startBounds.Y + dy;
                bounds.Height = _startBounds.Height - dy;
                if (bounds.Height < minimumHeight)
                {
                    bounds.Y = _startBounds.Bottom - minimumHeight;
                    bounds.Height = minimumHeight;
                }
            }
            else if (bottom)
                bounds.Height = Math.Max(minimumHeight, _startBounds.Height + dy);

            _form.Bounds = bounds;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
                EndDrag();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture)
                EndDrag();
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            if (_dragCompleted != null)
                _dragCompleted();
        }
    }

    internal sealed class AccentTile : Panel
    {
        private Color _accentColor = Color.DodgerBlue;
        private bool _darkTheme = true;

        public Color AccentColor
        {
            get { return _accentColor; }
            set { _accentColor = value; Invalidate(); }
        }

        public bool DarkTheme
        {
            get { return _darkTheme; }
            set { _darkTheme = value; Invalidate(); }
        }

        public AccentTile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? Color.Transparent : Parent.BackColor);
            RectangleF bounds = new RectangleF(0.5F, 0.5F, Math.Max(1F, Width - 1.5F), Math.Max(1F, Height - 1.5F));
            Color surface = Blend(DarkTheme ? Color.FromArgb(27, 29, 34) : Color.White,
                AccentColor, DarkTheme ? 0.25F : 0.14F);
            using (GraphicsPath path = CreateRoundedPath(bounds, 10F))
            using (SolidBrush fill = new SolidBrush(surface))
                e.Graphics.FillPath(fill, path);
        }

        private static Color Blend(Color first, Color second, float amount)
        {
            return Color.FromArgb(
                (int)(first.R + (second.R - first.R) * amount),
                (int)(first.G + (second.G - first.G) * amount),
                (int)(first.B + (second.B - first.B) * amount));
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            float diameter = radius * 2F;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180F, 90F);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270F, 90F);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0F, 90F);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90F, 90F);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class ColorSwatchButton : Button
    {
        public ColorSwatchButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? Color.Transparent : Parent.BackColor);
            RectangleF bounds = new RectangleF(0.5F, 0.5F, Math.Max(1F, Width - 1.5F), Math.Max(1F, Height - 1.5F));
            using (GraphicsPath path = CreateRoundedPath(bounds, 6F))
            using (SolidBrush fill = new SolidBrush(BackColor))
                e.Graphics.FillPath(fill, path);
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            float diameter = radius * 2F;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180F, 90F);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270F, 90F);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0F, 90F);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90F, 90F);
            path.CloseFigure();
            return path;
        }
    }

    internal enum CaptionButtonKind
    {
        Minimize,
        Maximize,
        Close
    }

    internal sealed class CaptionButton : Button
    {
        private bool _hovered;
        private bool _pressed;
        private bool _restoreGlyph;

        public CaptionButtonKind Kind { get; private set; }

        public bool RestoreGlyph
        {
            get { return _restoreGlyph; }
            set { if (_restoreGlyph != value) { _restoreGlyph = value; Invalidate(); } }
        }

        public CaptionButton(CaptionButtonKind kind)
        {
            Kind = kind;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = false;
            UseVisualStyleBackColor = false;
        }

        protected override bool ShowFocusCues { get { return false; } }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left) _pressed = true;
            Invalidate();
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color background = _pressed ? FlatAppearance.MouseDownBackColor :
                (_hovered ? FlatAppearance.MouseOverBackColor : BackColor);
            using (SolidBrush brush = new SolidBrush(background))
                e.Graphics.FillRectangle(brush, ClientRectangle);

            float scale = Math.Max(1F, DeviceDpi / 96F);
            float glyphSize = 10F * scale;
            float centerX = ClientSize.Width / 2F;
            float centerY = ClientSize.Height / 2F;
            using (Pen pen = new Pen(ForeColor, Math.Max(1F, 1.15F * scale)))
            {
                pen.StartCap = LineCap.Square;
                pen.EndCap = LineCap.Square;
                if (Kind == CaptionButtonKind.Minimize)
                {
                    e.Graphics.DrawLine(pen, centerX - glyphSize / 2F, centerY + glyphSize / 3F,
                        centerX + glyphSize / 2F, centerY + glyphSize / 3F);
                }
                else if (Kind == CaptionButtonKind.Close)
                {
                    float half = glyphSize / 2F;
                    e.Graphics.DrawLine(pen, centerX - half, centerY - half, centerX + half, centerY + half);
                    e.Graphics.DrawLine(pen, centerX + half, centerY - half, centerX - half, centerY + half);
                }
                else if (RestoreGlyph)
                {
                    float size = glyphSize - 2F * scale;
                    RectangleF back = new RectangleF(centerX - size / 2F + 2F * scale,
                        centerY - size / 2F - 2F * scale, size, size);
                    RectangleF front = new RectangleF(centerX - size / 2F - 2F * scale,
                        centerY - size / 2F + 2F * scale, size, size);
                    e.Graphics.DrawRectangle(pen, back.X, back.Y, back.Width, back.Height);
                    using (SolidBrush fill = new SolidBrush(background))
                        e.Graphics.FillRectangle(fill, front);
                    e.Graphics.DrawRectangle(pen, front.X, front.Y, front.Width, front.Height);
                }
                else
                {
                    RectangleF bounds = new RectangleF(centerX - glyphSize / 2F,
                        centerY - glyphSize / 2F, glyphSize, glyphSize);
                    e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                }
            }
        }
    }

    internal sealed class ThemedColorDialog : Form
    {
        private readonly bool _darkTheme;
        private readonly Panel _preview;
        private readonly NumericUpDown _red;
        private readonly NumericUpDown _green;
        private readonly NumericUpDown _blue;
        private readonly TextBox _hex;
        private readonly List<Button> _paletteButtons = new List<Button>();
        private bool _updating;
        private Color _selectedColor;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
            ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string subAppName,
            string subIdList);

        public Color SelectedColor
        {
            get { return _selectedColor; }
            private set
            {
                _selectedColor = value;
                UpdateColorControls();
            }
        }

        public ThemedColorDialog(Color initialColor, bool darkTheme)
        {
            _darkTheme = darkTheme;
            Color window = darkTheme ? Color.FromArgb(31, 33, 38) : Color.FromArgb(245, 247, 250);
            Color surface = darkTheme ? Color.FromArgb(42, 44, 51) : Color.White;
            Color text = darkTheme ? Color.FromArgb(245, 246, 248) : Color.FromArgb(25, 28, 34);
            Color secondary = darkTheme ? Color.FromArgb(170, 175, 187) : Color.FromArgb(78, 85, 98);
            Color border = darkTheme ? Color.FromArgb(68, 72, 82) : Color.FromArgb(205, 211, 220);

            Text = "Seleccionar color";
            ClientSize = new Size(470, 340);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = window;
            ForeColor = text;
            Font = new Font("Segoe UI", 9F);

            Label paletteTitle = CreateDialogLabel("Colores", new Point(24, 20), text, true);
            Controls.Add(paletteTitle);
            Color[] palette =
            {
                Color.FromArgb(0, 188, 242), Color.FromArgb(0, 122, 204),
                Color.FromArgb(46, 204, 113), Color.FromArgb(0, 200, 180),
                Color.FromArgb(255, 215, 0), Color.FromArgb(255, 152, 0),
                Color.FromArgb(239, 83, 80), Color.FromArgb(233, 30, 99),
                Color.FromArgb(166, 112, 255), Color.FromArgb(126, 87, 194),
                Color.FromArgb(63, 81, 181), Color.FromArgb(72, 181, 199),
                Color.FromArgb(255, 255, 255), Color.FromArgb(189, 189, 189),
                Color.FromArgb(96, 100, 110), Color.FromArgb(31, 33, 38),
                Color.FromArgb(244, 67, 54), Color.FromArgb(121, 85, 72),
                Color.FromArgb(139, 195, 74), Color.FromArgb(0, 150, 136),
                Color.FromArgb(3, 169, 244), Color.FromArgb(103, 58, 183),
                Color.FromArgb(156, 39, 176), Color.FromArgb(255, 105, 180)
            };
            for (int index = 0; index < palette.Length; index++)
            {
                Button colorButton = new Button
                {
                    BackColor = palette[index],
                    FlatStyle = FlatStyle.Flat,
                    Size = new Size(38, 30),
                    Location = new Point(24 + (index % 8) * 48, 50 + (index / 8) * 40),
                    Cursor = Cursors.Hand,
                    TabStop = false,
                    Tag = palette[index],
                    AccessibleName = "Seleccionar " + ColorTranslator.ToHtml(palette[index])
                };
                colorButton.FlatAppearance.BorderColor = border;
                colorButton.FlatAppearance.BorderSize = 1;
                colorButton.Click += PaletteColorClicked;
                _paletteButtons.Add(colorButton);
                Controls.Add(colorButton);
            }

            Panel separator = new Panel
            {
                BackColor = border,
                Location = new Point(24, 180),
                Size = new Size(422, 1)
            };
            Controls.Add(separator);
            Controls.Add(CreateDialogLabel("Color personalizado", new Point(24, 198), text, true));

            _preview = new Panel
            {
                Location = new Point(24, 228),
                Size = new Size(72, 48),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_preview);
            _red = CreateColorNumber(126, 239, surface, text, border);
            _green = CreateColorNumber(206, 239, surface, text, border);
            _blue = CreateColorNumber(286, 239, surface, text, border);
            Controls.Add(CreateDialogLabel("R", new Point(110, 243), secondary, false));
            Controls.Add(CreateDialogLabel("G", new Point(190, 243), secondary, false));
            Controls.Add(CreateDialogLabel("B", new Point(270, 243), secondary, false));
            Controls.Add(_red);
            Controls.Add(_green);
            Controls.Add(_blue);
            _red.ValueChanged += RgbValueChanged;
            _green.ValueChanged += RgbValueChanged;
            _blue.ValueChanged += RgbValueChanged;

            _hex = new TextBox
            {
                Location = new Point(366, 239),
                Size = new Size(80, 25),
                BackColor = surface,
                ForeColor = text,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center,
                CharacterCasing = CharacterCasing.Upper,
                MaxLength = 7
            };
            Controls.Add(CreateDialogLabel("HEX", new Point(328, 243), secondary, false));
            Controls.Add(_hex);
            _hex.TextChanged += HexTextChanged;

            Button cancel = CreateDialogButton("Cancelar", new Point(262, 294), 88,
                surface, text, border);
            cancel.DialogResult = DialogResult.Cancel;
            Button accept = CreateDialogButton("Aceptar", new Point(358, 294), 88,
                Color.FromArgb(0, 122, 204), Color.White, Color.FromArgb(0, 122, 204));
            accept.DialogResult = DialogResult.OK;
            Controls.Add(cancel);
            Controls.Add(accept);
            CancelButton = cancel;
            AcceptButton = accept;
            SelectedColor = initialColor;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int enabled = _darkTheme ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
                SetWindowTheme(Handle, _darkTheme ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }

        private static Label CreateDialogLabel(string text, Point location, Color color, bool heading)
        {
            return new Label
            {
                Text = text,
                Location = location,
                ForeColor = color,
                Font = new Font("Segoe UI" + (heading ? " Semibold" : ""), heading ? 10F : 9F),
                AutoSize = true
            };
        }

        private static NumericUpDown CreateColorNumber(int x, int y, Color background,
            Color text, Color border)
        {
            return new NumericUpDown
            {
                Location = new Point(x, y),
                Size = new Size(58, 25),
                Minimum = 0,
                Maximum = 255,
                BackColor = background,
                ForeColor = text,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center
            };
        }

        private static Button CreateDialogButton(string text, Point location, int width,
            Color background, Color foreground, Color border)
        {
            Button button = new Button
            {
                Text = text,
                Location = location,
                Size = new Size(width, 30),
                BackColor = background,
                ForeColor = foreground,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.BorderSize = 1;
            return button;
        }

        private void PaletteColorClicked(object sender, EventArgs e)
        {
            Button button = sender as Button;
            if (button != null && button.Tag is Color) SelectedColor = (Color)button.Tag;
        }

        private void RgbValueChanged(object sender, EventArgs e)
        {
            if (_updating) return;
            SelectedColor = Color.FromArgb((int)_red.Value, (int)_green.Value, (int)_blue.Value);
        }

        private void HexTextChanged(object sender, EventArgs e)
        {
            if (_updating) return;
            string text = (_hex.Text ?? "").Trim().TrimStart('#');
            int value;
            if (text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out value))
                SelectedColor = Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255);
        }

        private void UpdateColorControls()
        {
            if (_preview == null) return;
            _updating = true;
            try
            {
                _preview.BackColor = _selectedColor;
                _red.Value = _selectedColor.R;
                _green.Value = _selectedColor.G;
                _blue.Value = _selectedColor.B;
                _hex.Text = "#" + _selectedColor.R.ToString("X2") +
                    _selectedColor.G.ToString("X2") + _selectedColor.B.ToString("X2");
                foreach (Button button in _paletteButtons)
                {
                    Color item = (Color)button.Tag;
                    button.FlatAppearance.BorderSize = item.ToArgb() == _selectedColor.ToArgb() ? 3 : 1;
                    button.FlatAppearance.BorderColor = item.ToArgb() == _selectedColor.ToArgb()
                        ? (_darkTheme ? Color.White : Color.Black)
                        : (_darkTheme ? Color.FromArgb(68, 72, 82) : Color.FromArgb(205, 211, 220));
                }
            }
            finally { _updating = false; }
        }
    }

    internal sealed class InfoIconButton : Button
    {
        public InfoIconButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Max(1F, DeviceDpi / 96F);
            float diameter = Math.Min(18F * scale, Math.Min(Width, Height) - 6F * scale);
            float left = (Width - diameter) / 2F;
            float top = (Height - diameter) / 2F;
            using (Pen pen = new Pen(ForeColor, Math.Max(1.6F, 1.65F * scale)))
            using (SolidBrush brush = new SolidBrush(ForeColor))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                e.Graphics.DrawEllipse(pen, left, top, diameter, diameter);
                float centerX = Width / 2F;
                float dot = Math.Max(2.2F, 2.2F * scale);
                e.Graphics.FillEllipse(brush, centerX - dot / 2F,
                    top + diameter * 0.25F - dot / 2F, dot, dot);
                e.Graphics.DrawLine(pen, centerX, top + diameter * 0.46F,
                    centerX, top + diameter * 0.73F);
            }
        }
    }

    internal sealed class DeviceInfoDialog : Form
    {
        private readonly bool _darkTheme;
        private readonly ToolTip _detailToolTip = new ToolTip();

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
            ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string subAppName,
            string subIdList);

        public DeviceInfoDialog(Form owner, bool darkTheme, HardwareIconKind kind, Color accent,
            string title, string subtitle, string current, string minimum, string maximum,
            string status, IList<KeyValuePair<string, string>> details)
        {
            _darkTheme = darkTheme;
            Color window = darkTheme ? Color.FromArgb(31, 33, 38) : Color.FromArgb(245, 247, 250);
            Color surface = darkTheme ? Color.FromArgb(40, 42, 48) : Color.White;
            Color primary = darkTheme ? Color.FromArgb(245, 246, 248) : Color.FromArgb(25, 28, 34);
            Color secondary = darkTheme ? Color.FromArgb(170, 175, 187) : Color.FromArgb(78, 85, 98);
            Color border = darkTheme ? Color.FromArgb(62, 66, 75) : Color.FromArgb(210, 215, 223);
            int detailCount = Math.Max(3, Math.Min(8, details == null ? 0 : details.Count));
            int detailsHeight = detailCount * 38 + 2;
            int closeTop = 241 + detailsHeight + 18;

            Text = "Información del dispositivo";
            ClientSize = new Size(540, closeTop + 46);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = window;
            ForeColor = primary;
            Font = new Font("Segoe UI", 9F);
            if (owner != null && owner.Icon != null) Icon = owner.Icon;

            Controls.Add(new Panel
            {
                BackColor = accent,
                Location = new Point(0, 0),
                Size = new Size(ClientSize.Width, 4),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            });

            AccentTile iconTile = new AccentTile
            {
                AccentColor = accent,
                DarkTheme = darkTheme,
                Location = new Point(24, 20),
                Size = new Size(52, 52)
            };
            HardwareIcon icon = new HardwareIcon
            {
                Kind = kind,
                ForeColor = accent,
                Location = new Point(7, 7),
                Size = new Size(38, 38)
            };
            iconTile.Controls.Add(icon);
            Controls.Add(iconTile);
            Controls.Add(CreateText(title, new Point(92, 19), new Size(410, 30), primary,
                new Font("Segoe UI Semibold", 15F), ContentAlignment.MiddleLeft));
            Controls.Add(CreateText(subtitle, new Point(92, 48), new Size(410, 23), secondary,
                new Font("Segoe UI", 9.5F), ContentAlignment.MiddleLeft));

            int statWidth = 154;
            AddStatCard("ACTUAL", current, 24, 90, statWidth, surface, primary, secondary, border, accent, true);
            AddStatCard("MÍNIMA", minimum, 193, 90, statWidth, surface, primary, secondary, border, accent, false);
            AddStatCard("MÁXIMA", maximum, 362, 90, statWidth, surface, primary, secondary, border, accent, false);

            Label statusLabel = CreateText(Safe(status), new Point(24, 178), new Size(492, 24),
                GetStatusColor(status), new Font("Segoe UI Semibold", 9.5F), ContentAlignment.MiddleRight);
            Controls.Add(statusLabel);
            Controls.Add(CreateText("Información del hardware", new Point(24, 211), new Size(492, 24),
                primary, new Font("Segoe UI Semibold", 10F), ContentAlignment.MiddleLeft));

            Panel detailsPanel = new Panel
            {
                Location = new Point(24, 241),
                Size = new Size(492, detailsHeight),
                BackColor = surface
            };
            Controls.Add(detailsPanel);
            int row = 0;
            foreach (KeyValuePair<string, string> detail in (details ??
                new List<KeyValuePair<string, string>>()).Take(8))
            {
                int y = row * 38;
                if (row > 0)
                    detailsPanel.Controls.Add(new Panel
                    {
                        BackColor = border,
                        Location = new Point(12, y),
                        Size = new Size(detailsPanel.Width - 24, 1)
                    });
                detailsPanel.Controls.Add(CreateText(detail.Key, new Point(14, y + 7), new Size(122, 25),
                    secondary, new Font("Segoe UI", 8.8F), ContentAlignment.MiddleLeft));
                Label detailValue = CreateText(Safe(detail.Value), new Point(140, y + 7),
                    new Size(detailsPanel.Width - 154, 25), primary,
                    new Font("Segoe UI Semibold", 8.8F), ContentAlignment.MiddleLeft);
                detailValue.AutoEllipsis = true;
                detailsPanel.Controls.Add(detailValue);
                _detailToolTip.SetToolTip(detailValue, Safe(detail.Value));
                row++;
            }

            Button close = new Button
            {
                Text = "Cerrar",
                DialogResult = DialogResult.OK,
                Location = new Point(416, closeTop),
                Size = new Size(100, 30),
                BackColor = accent,
                ForeColor = BestTextColor(accent),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            close.FlatAppearance.BorderColor = accent;
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _detailToolTip.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int enabled = _darkTheme ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
                SetWindowTheme(Handle, _darkTheme ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }

        private void AddStatCard(string caption, string value, int x, int y, int width,
            Color surface, Color primary, Color secondary, Color border, Color accent, bool current)
        {
            Panel panel = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(width, 75),
                BackColor = surface,
                BorderStyle = BorderStyle.FixedSingle
            };
            panel.Controls.Add(CreateText(caption, new Point(12, 7), new Size(width - 24, 18), secondary,
                new Font("Segoe UI Semibold", 8F), ContentAlignment.MiddleLeft));
            panel.Controls.Add(CreateText(Safe(value), new Point(10, 26), new Size(width - 20, 39),
                current ? accent : primary, current ? DigitalFontProvider.Create(23F) :
                new Font("Segoe UI Semibold", 16F), ContentAlignment.MiddleLeft));
            Controls.Add(panel);
        }

        private static Label CreateText(string text, Point location, Size size, Color color,
            Font font, ContentAlignment alignment)
        {
            return new Label
            {
                Text = Safe(text),
                Location = location,
                Size = size,
                ForeColor = color,
                Font = font,
                TextAlign = alignment,
                BackColor = Color.Transparent,
                UseCompatibleTextRendering = false
            };
        }

        private static string Safe(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? "No disponible" : text.Trim();
        }

        private static Color GetStatusColor(string status)
        {
            string value = status ?? "";
            if (value.IndexOf("subiendo", StringComparison.OrdinalIgnoreCase) >= 0)
                return Color.FromArgb(239, 83, 80);
            if (value.IndexOf("bajando", StringComparison.OrdinalIgnoreCase) >= 0)
                return Color.FromArgb(46, 204, 113);
            return Color.FromArgb(72, 181, 199);
        }

        private static Color BestTextColor(Color background)
        {
            double luminance = 0.299 * background.R + 0.587 * background.G + 0.114 * background.B;
            return luminance > 165 ? Color.FromArgb(20, 22, 26) : Color.White;
        }
    }

    internal sealed class SensorCardPanel : Panel
    {
        private Color _accentColor = Color.DodgerBlue;
        private bool _darkTheme = true;

        public string GroupName { get; set; }
        public IList<KeyValuePair<string, string>> DeviceDetails { get; set; }

        public Color AccentColor
        {
            get { return _accentColor; }
            set { _accentColor = value; Invalidate(); }
        }

        public bool DarkTheme
        {
            get { return _darkTheme; }
            set
            {
                _darkTheme = value;
                BackColor = value ? Color.FromArgb(27, 29, 34) : Color.White;
                Invalidate();
            }
        }

        public SensorCardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor, true);
            BorderStyle = BorderStyle.None;
            BackColor = Color.FromArgb(27, 29, 34);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color outside = Parent == null ? BackColor : Parent.BackColor;
            e.Graphics.Clear(outside);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(1F, 1F, Math.Max(1F, Width - 2F), Math.Max(1F, Height - 2F));
            using (GraphicsPath path = CreateRoundedPath(bounds, 14F))
            using (SolidBrush background = new SolidBrush(DarkTheme ? Color.FromArgb(27, 29, 34) : Color.White))
                e.Graphics.FillPath(background, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF outer = new RectangleF(1F, 1F, Math.Max(1F, Width - 2.5F), Math.Max(1F, Height - 2.5F));
            Color borderColor = DarkTheme ? Color.FromArgb(60, 64, 72) : Color.FromArgb(203, 210, 220);
            using (GraphicsPath path = CreateRoundedPath(outer, 14F))
            using (Pen border = new Pen(borderColor, 1F))
                e.Graphics.DrawPath(border, path);
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            float diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2F);
            GraphicsPath path = new GraphicsPath();
            if (diameter <= 1F)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180F, 90F);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270F, 90F);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0F, 90F);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90F, 90F);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class DashboardForm : Form
    {
        private const int MinCardWidth = 270;
        private const int MaxCardWidth = 430;
        private const int CardHeight = 280;
        private const int CardStrideY = CardHeight + 12;
        private const int GroupHeaderHeight = 42;
        private const int GroupHeaderStride = GroupHeaderHeight + 14;
        private const int CustomTitleHeight = 34;
        private const int CustomMenuHeight = 28;
        private const int AdaptiveBottomSpace = 66;
        private static readonly object CardColorSync = new object();
        private static readonly Dictionary<string, Color> CardColorCache = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        // Todas las tarjetas comparten la misma tipografía. Mantener una instancia por
        // estilo evita crear decenas de objetos GDI idénticos al detectar componentes.
        private static readonly Font CardTitleFont = new Font("Segoe UI Semibold", 10.5F);
        private static readonly Font CardSubtitleFont = new Font("Segoe UI", 8.8F);
        private static readonly Font CardValueFont = DigitalFontProvider.Create(36F);
        private static readonly Font CardUnitFont = new Font("Segoe UI", 18F, FontStyle.Regular, GraphicsUnit.Point);
        private static readonly Font CardStatusFont = new Font("Segoe UI Semibold", 9F);
        private static readonly Font CardSensorFont = new Font("Segoe UI", 8.5F);
        private static readonly Font CardTrayFont = new Font("Segoe UI Semibold", 9.3F);
        private readonly Label _cpuValue;
        private readonly Label _gpuValue;
        private readonly Label _boardValue;
        private readonly Label _cpuTitle;
        private readonly Label _gpuTitle;
        private readonly Label _boardTitle;
        private readonly TemperatureSparkline _cpuGraph;
        private readonly TemperatureSparkline _gpuGraph;
        private readonly TemperatureSparkline _boardGraph;
        private readonly Label _cpuSensor;
        private readonly Label _gpuSensor;
        private readonly Label _boardSensor;
        private readonly FlowLayoutPanel _cardsFlow;
        private readonly Panel _memoryGroupHeader;
        private readonly Panel _diskGroupHeader;
        private readonly Dictionary<string, SensorCardView> _diskCards = new Dictionary<string, SensorCardView>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SensorCardView> _memoryCards = new Dictionary<string, SensorCardView>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, bool> _isTraySelected;
        private readonly Action<string, bool> _setTraySelected;
        private readonly Action<bool> _setStartup;
        private readonly Action<string, Color> _setCardColor;
        private readonly Action<string> _setBoardSensor;
        private readonly Panel _windowChrome;
        private readonly Panel _contentPanel;
        private readonly ThemedVScrollBar _verticalScrollBar;
        private readonly Panel _titleBar;
        private readonly PictureBox _titleIcon;
        private readonly Label _titleCaption;
        private readonly Button _minimizeButton;
        private readonly Button _maximizeButton;
        private readonly Button _closeButton;
        private readonly MenuStrip _mainMenu;
        private readonly ToolStripMenuItem _optionsMenu;
        private readonly ToolStripMenuItem _startupMenuItem;
        private readonly ToolStripMenuItem _errorReportsMenuItem;
        private readonly ToolStripMenuItem _themeMenuItem;
        private readonly ToolStripMenuItem _darkThemeMenuItem;
        private readonly ToolStripMenuItem _boardSensorMenuItem;
        private readonly ToolStripMenuItem _autoBoardSensorMenuItem;
        private readonly ToolStripMenuItem _noBoardSensorsMenuItem;
        private readonly Dictionary<string, ToolStripMenuItem> _boardSensorMenuItems =
            new Dictionary<string, ToolStripMenuItem>(StringComparer.OrdinalIgnoreCase);
        private readonly ToolStripMenuItem _trayMenuItem;
        private readonly ToolStripMenuItem _helpMenu;
        private readonly ToolStripMenuItem _aboutMenuItem;
        private bool _darkTheme;
        private bool _updatingStartup;
        private bool _updatingTrayToggles;
        private readonly Button _driverButton;
        private readonly Button _elevateButton;
        private readonly Panel _bottomSpacer;
        private int _lastCardCount;
        private bool _layingOut;
        private bool _applyingSavedOrder;
        private Point _dragStart;
        private Panel _dragCard;
        private Control _dragCaptureControl;
        private bool _cardDragStarted;
        private Point _dragCardOffset;
        private CardDragGhost _dragGhost;
        private DateTime _lastGraphSample;
        private readonly List<WindowResizeGrip> _resizeGrips = new List<WindowResizeGrip>();
        private readonly List<int> _visibleCardWidths = new List<int>();
        private readonly ToolTip _cardToolTip;
        private FormWindowState _lastWindowState = FormWindowState.Normal;
        private bool _restoreLayoutPending;
        private bool _gpuAvailable = true;
        private bool _memoryGroupCollapsed;
        private bool _diskGroupCollapsed;
        private int _lastMemoryGroupCount = -1;
        private int _lastDiskGroupCount = -1;
        private bool _lastMemoryGroupCollapsed;
        private bool _lastDiskGroupCollapsed;

        private sealed class SensorCardView
        {
            public Panel Panel;
            public Label Value;
            public Label Sensor;
            public TemperatureSparkline Graph;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string subAppName, string subIdList);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBarTheme();
        }

        public DashboardForm(Action installSensorDriver, Action requestElevation, Action openTraySettings,
            Func<string, bool> isTraySelected = null, Action<string, bool> setTraySelected = null,
            Action<bool> setStartup = null, Action<string, Color> setCardColor = null,
            Action<string> setBoardSensor = null)
        {
            _isTraySelected = isTraySelected;
            _setTraySelected = setTraySelected;
            _setStartup = setStartup;
            _setCardColor = setCardColor;
            _setBoardSensor = setBoardSensor;
            _darkTheme = LoadDarkTheme();
            _memoryGroupCollapsed = LoadGroupCollapsed("MemoryGroupCollapsed");
            _diskGroupCollapsed = LoadGroupCollapsed("DiskGroupCollapsed");
            Text = "PcTemp " + Program.AppVersion + " · Última actualización: pendiente";
            FormBorderStyle = FormBorderStyle.None;
            Size = new Size(610, 500);
            MinimumSize = new Size(660, 430);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = _darkTheme ? Color.FromArgb(38, 40, 45) : Color.FromArgb(229, 233, 239);
            ForeColor = _darkTheme ? Color.White : Color.FromArgb(24, 26, 31);
            Font = new Font("Segoe UI", 8.5F);
            ShowInTaskbar = true;
            MaximizeBox = true;
            AutoScroll = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _windowChrome = new Panel
            {
                Dock = DockStyle.Top,
                Height = CustomTitleHeight + CustomMenuHeight,
                TabStop = false
            };
            _titleBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = CustomTitleHeight,
                TabStop = false
            };
            _titleIcon = new PictureBox
            {
                Location = new Point(10, 8),
                Size = new Size(18, 18),
                SizeMode = PictureBoxSizeMode.Zoom,
                TabStop = false
            };
            try { if (Icon != null) _titleIcon.Image = Icon.ToBitmap(); } catch { }
            _titleCaption = new Label
            {
                AutoSize = false,
                Location = new Point(36, 0),
                Height = CustomTitleHeight,
                Text = Text,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 9.5F),
                AutoEllipsis = true
            };
            _minimizeButton = CreateCaptionButton(CaptionButtonKind.Minimize);
            _maximizeButton = CreateCaptionButton(CaptionButtonKind.Maximize);
            _closeButton = CreateCaptionButton(CaptionButtonKind.Close);
            _minimizeButton.AccessibleName = "Minimizar";
            _maximizeButton.AccessibleName = "Maximizar o restaurar";
            _closeButton.AccessibleName = "Cerrar";
            _minimizeButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            _maximizeButton.Click += delegate { ToggleMaximize(); };
            _closeButton.Click += delegate { Close(); };
            _titleBar.MouseDown += TitleBarMouseDown;
            _titleCaption.MouseDown += TitleBarMouseDown;
            _titleIcon.MouseDown += TitleBarMouseDown;
            _titleBar.Controls.Add(_titleIcon);
            _titleBar.Controls.Add(_titleCaption);
            _titleBar.Controls.Add(_minimizeButton);
            _titleBar.Controls.Add(_maximizeButton);
            _titleBar.Controls.Add(_closeButton);
            _titleBar.Resize += delegate { LayoutCaptionControls(); };
            _windowChrome.Controls.Add(_titleBar);

            _mainMenu = new MenuStrip
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 28,
                GripStyle = ToolStripGripStyle.Hidden,
                Padding = new Padding(12, 2, 0, 2),
                Font = new Font("Segoe UI", 8.5F)
            };
            _cardToolTip = new ToolTip
            {
                InitialDelay = 350,
                ReshowDelay = 150,
                AutoPopDelay = 5000,
                ShowAlways = true
            };
            _optionsMenu = new ToolStripMenuItem("Opciones")
            {
                DropDownDirection = ToolStripDropDownDirection.BelowRight
            };
            _startupMenuItem = new ToolStripMenuItem("Iniciar con Windows") { CheckOnClick = true };
            _startupMenuItem.CheckedChanged += delegate
            {
                if (!_updatingStartup && _setStartup != null)
                    _setStartup(_startupMenuItem.Checked);
            };
            _errorReportsMenuItem = new ToolStripMenuItem("Enviar informes anónimos de fallos")
            {
                CheckOnClick = true,
                Checked = CrashReporter.ConsentEnabled
            };
            _errorReportsMenuItem.CheckedChanged += delegate
            {
                CrashReporter.SetConsent(_errorReportsMenuItem.Checked);
            };
            _themeMenuItem = new ToolStripMenuItem("Tema claro")
            {
                Checked = !_darkTheme
            };
            _themeMenuItem.Click += delegate
            {
                SelectTheme(false);
            };
            _darkThemeMenuItem = new ToolStripMenuItem("Tema oscuro")
            {
                Checked = _darkTheme
            };
            _darkThemeMenuItem.Click += delegate
            {
                SelectTheme(true);
            };
            _boardSensorMenuItem = new ToolStripMenuItem("Sensor de placa base");
            _autoBoardSensorMenuItem = new ToolStripMenuItem("Automático · priorizar MotherBoard Temperature")
            {
                Checked = true,
                Tag = ""
            };
            _autoBoardSensorMenuItem.Click += BoardSensorMenuClicked;
            _noBoardSensorsMenuItem = new ToolStripMenuItem("No hay sensores de placa identificables")
            {
                Enabled = false
            };
            _boardSensorMenuItem.DropDownItems.Add(_autoBoardSensorMenuItem);
            _boardSensorMenuItem.DropDownItems.Add(new ToolStripSeparator());
            _boardSensorMenuItem.DropDownItems.Add(_noBoardSensorsMenuItem);
            _trayMenuItem = new ToolStripMenuItem("Activar icono PcTemp");
            _trayMenuItem.Click += delegate { if (openTraySettings != null) openTraySettings(); };
            _optionsMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                _startupMenuItem, _errorReportsMenuItem, new ToolStripSeparator(),
                _themeMenuItem, _darkThemeMenuItem, _boardSensorMenuItem,
                new ToolStripSeparator(), _trayMenuItem
            });
            _helpMenu = new ToolStripMenuItem("Ayuda")
            {
                DropDownDirection = ToolStripDropDownDirection.BelowRight
            };
            _aboutMenuItem = new ToolStripMenuItem("Acerca de PcTemp");
            _aboutMenuItem.Click += delegate
            {
                using (AboutForm about = new AboutForm(this, _darkTheme))
                    about.ShowDialog(this);
            };
            _helpMenu.DropDownItems.Add(_aboutMenuItem);
            _mainMenu.Items.AddRange(new ToolStripItem[] { _optionsMenu, _helpMenu });
            MainMenuStrip = _mainMenu;
            _mainMenu.Dock = DockStyle.Bottom;
            _windowChrome.Controls.Add(_mainMenu);
            LayoutCaptionControls();

            _contentPanel = new Panel
            {
                Location = new Point(0, _windowChrome.Height),
                Size = new Size(ClientSize.Width, Math.Max(1, ClientSize.Height - _windowChrome.Height)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = false,
                TabStop = false
            };
            _verticalScrollBar = new ThemedVScrollBar
            {
                Width = 18,
                Visible = false,
                TabStop = false,
                SmallChange = 76
            };
            _verticalScrollBar.ValueChanged += delegate { ReflowLayout(); };
            Controls.Add(_contentPanel);
            Controls.Add(_verticalScrollBar);
            Controls.Add(_windowChrome);
            CreateResizeGrips();

            _cpuValue = new DigitalValueLabel(); _cpuSensor = new Label();
            _gpuValue = new DigitalValueLabel(); _gpuSensor = new Label();
            _boardValue = new DigitalValueLabel(); _boardSensor = new Label();
            _cardsFlow = new FlowLayoutPanel
            {
                Location = new Point(10, 62),
                Size = new Size(570, CardStrideY),
                BackColor = Color.FromArgb(24, 24, 27),
                AutoScroll = false,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0)
            };
            Panel cpuCard = CreateCard("CPU", "Detectando…", Color.DeepSkyBlue, _cpuValue, _cpuSensor, "cpu");
            Panel gpuCard = CreateCard("GPU", "Detectando…", Color.Gainsboro, _gpuValue, _gpuSensor, "gpu");
            Panel boardCard = CreateCard("Placa base", "Detectando…", Color.Gold, _boardValue, _boardSensor, "board");
            ((SensorCardPanel)cpuCard).GroupName = "core";
            ((SensorCardPanel)gpuCard).GroupName = "core";
            ((SensorCardPanel)boardCard).GroupName = "core";
            _cpuTitle = (Label)cpuCard.Controls["CardTitle"];
            _gpuTitle = (Label)gpuCard.Controls["CardTitle"];
            _boardTitle = (Label)boardCard.Controls["CardTitle"];
            _cpuGraph = (TemperatureSparkline)cpuCard.Controls["Sparkline"];
            _gpuGraph = (TemperatureSparkline)gpuCard.Controls["Sparkline"];
            _boardGraph = (TemperatureSparkline)boardCard.Controls["Sparkline"];
            cpuCard.Margin = gpuCard.Margin = boardCard.Margin = new Padding(0, 0, 6, 10);
            _cardsFlow.Controls.Add(cpuCard);
            _cardsFlow.Controls.Add(gpuCard);
            _cardsFlow.Controls.Add(boardCard);
            _memoryGroupHeader = CreateGroupHeader("Memoria RAM", "group:memory", delegate
            {
                _memoryGroupCollapsed = !_memoryGroupCollapsed;
                SaveGroupCollapsed("MemoryGroupCollapsed", _memoryGroupCollapsed);
                ApplyGroupVisibility();
            });
            _diskGroupHeader = CreateGroupHeader("Discos", "group:disk", delegate
            {
                _diskGroupCollapsed = !_diskGroupCollapsed;
                SaveGroupCollapsed("DiskGroupCollapsed", _diskGroupCollapsed);
                ApplyGroupVisibility();
            });
            _memoryGroupHeader.Visible = false;
            _diskGroupHeader.Visible = false;
            _cardsFlow.Controls.Add(_memoryGroupHeader);
            _cardsFlow.Controls.Add(_diskGroupHeader);
            _cardsFlow.SetFlowBreak(_memoryGroupHeader, true);
            _cardsFlow.SetFlowBreak(_diskGroupHeader, true);
            _contentPanel.MouseWheel += ContentMouseWheel;
            _cardsFlow.MouseWheel += ContentMouseWheel;
            ApplySavedCardOrder();
            _contentPanel.Controls.Add(_cardsFlow);

            _driverButton = CreateButton("Instalar acceso a sensores", 24, 345, 180);
            _driverButton.BackColor = Color.FromArgb(196, 116, 0);
            _driverButton.Visible = false;
            _driverButton.Click += delegate { if (installSensorDriver != null) installSensorDriver(); };
            _contentPanel.Controls.Add(_driverButton);

            _elevateButton = CreateButton("Reiniciar como administrador", 216, 345, 180);
            _elevateButton.BackColor = Color.FromArgb(0, 122, 204);
            _elevateButton.Visible = false;
            _elevateButton.Click += delegate { if (requestElevation != null) requestElevation(); };
            _contentPanel.Controls.Add(_elevateButton);

            _bottomSpacer = new Panel
            {
                BackColor = BackColor,
                Size = new Size(1, 40),
                TabStop = false
            };
            _contentPanel.Controls.Add(_bottomSpacer);
            _bottomSpacer.SendToBack();

            Resize += delegate
            {
                FormWindowState previousState = _lastWindowState;
                _lastWindowState = WindowState;
                ReflowLayout();
                RefreshMenuLayout();
                UpdateMaximizeButton();
                LayoutResizeGrips();
                if (previousState != FormWindowState.Normal &&
                    WindowState == FormWindowState.Normal && !_restoreLayoutPending)
                {
                    _restoreLayoutPending = true;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _restoreLayoutPending = false;
                        if (WindowState == FormWindowState.Normal)
                            ApplyAdaptiveWindowSize();
                    }));
                }
            };
            ResizeEnd += delegate
            {
                RefreshCustomFrame();
                KeepWindowOnScreen();
                RefreshMenuLayout();
            };
            Shown += delegate
            {
                RefreshCustomFrame();
                ApplyTitleBarTheme();
                KeepWindowOnScreen();
            };
            FormClosed += delegate
            {
                CloseDragGhost();
                _cardToolTip.Dispose();
            };
            _lastCardCount = _cardsFlow.Controls.Count;
            ApplyAdaptiveWindowSize();
            _windowChrome.BringToFront();
            LayoutResizeGrips();
            ApplyTheme();
        }

        private void CreateResizeGrips()
        {
            Action completed = delegate
            {
                RefreshCustomFrame();
                KeepWindowOnScreen();
                RefreshMenuLayout();
            };
            _resizeGrips.Add(new WindowResizeGrip(this, 12, Cursors.SizeNS, completed));
            _resizeGrips.Add(new WindowResizeGrip(this, 15, Cursors.SizeNS, completed));
            _resizeGrips.Add(new WindowResizeGrip(this, 10, Cursors.SizeWE, completed));
            _resizeGrips.Add(new WindowResizeGrip(this, 11, Cursors.SizeWE, completed));
            _resizeGrips.Add(new WindowResizeGrip(this, 13, Cursors.SizeNWSE, completed));
            _resizeGrips.Add(new WindowResizeGrip(this, 14, Cursors.SizeNESW, completed));
            _resizeGrips.Add(new WindowResizeGrip(this, 16, Cursors.SizeNESW, completed));
            _resizeGrips.Add(new WindowResizeGrip(this, 17, Cursors.SizeNWSE, completed));
            foreach (WindowResizeGrip grip in _resizeGrips)
                Controls.Add(grip);
        }

        private void LayoutResizeGrips()
        {
            if (_resizeGrips.Count != 8) return;
            bool visible = WindowState == FormWindowState.Normal;
            int edge = 6;
            int corner = 14;
            int width = ClientSize.Width;
            int height = ClientSize.Height;

            _resizeGrips[0].SetBounds(corner, 0, Math.Max(1, width - corner * 2), edge);
            _resizeGrips[1].SetBounds(corner, Math.Max(0, height - edge), Math.Max(1, width - corner * 2), edge);
            int sideTop = _windowChrome.Bottom;
            _resizeGrips[2].SetBounds(0, sideTop, edge, Math.Max(1, height - sideTop - corner));
            _resizeGrips[3].SetBounds(Math.Max(0, width - edge), sideTop, edge,
                Math.Max(1, height - sideTop - corner));
            _resizeGrips[4].SetBounds(0, 0, corner, corner);
            _resizeGrips[5].SetBounds(Math.Max(0, width - corner), 0, corner, corner);
            _resizeGrips[6].SetBounds(0, Math.Max(0, height - corner), corner, corner);
            _resizeGrips[7].SetBounds(Math.Max(0, width - corner), Math.Max(0, height - corner), corner, corner);
            for (int index = 0; index < _resizeGrips.Count; index++)
            {
                WindowResizeGrip grip = _resizeGrips[index];
                // Never place transparent resize controls over the custom title
                // bar: they would cover the hover backgrounds of the caption
                // buttons and create white notches in the rounded corners.
                bool overlapsTitleBar = index == 0 || index == 4 || index == 5;
                grip.Visible = visible && !overlapsTitleBar;
                grip.BringToFront();
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (_titleCaption != null && _titleCaption.Text != Text)
                _titleCaption.Text = Text;
        }

        private static Button CreateCaptionButton(CaptionButtonKind kind)
        {
            CaptionButton button = new CaptionButton(kind)
            {
                Size = new Size(46, CustomTitleHeight),
                Text = ""
            };
            return button;
        }

        private void LayoutCaptionControls()
        {
            if (_titleBar == null) return;
            int right = _titleBar.ClientSize.Width;
            _closeButton.Location = new Point(Math.Max(0, right - _closeButton.Width), 0);
            _maximizeButton.Location = new Point(Math.Max(0, _closeButton.Left - _maximizeButton.Width), 0);
            _minimizeButton.Location = new Point(Math.Max(0, _maximizeButton.Left - _minimizeButton.Width), 0);
            _titleCaption.Width = Math.Max(30, _minimizeButton.Left - _titleCaption.Left - 8);
        }

        private void ToggleMaximize()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }
            else
            {
                MaximizedBounds = Rectangle.Empty;
                WindowState = FormWindowState.Maximized;
            }
            UpdateMaximizeButton();
        }

        private void UpdateMaximizeButton()
        {
            CaptionButton maximize = _maximizeButton as CaptionButton;
            if (maximize != null)
                maximize.RestoreGlyph = WindowState == FormWindowState.Maximized;
            ApplyWindowCornerPreference();
        }

        private void TitleBarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks > 1)
            {
                ToggleMaximize();
                return;
            }

            ReleaseCapture();
            SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
            KeepWindowOnScreen();
        }

        private static Button CreateButton(string text, int x, int y, int width)
        {
            return new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 61),
                ForeColor = Color.White,
                TabStop = false,
                Cursor = Cursors.Hand
            };
        }

        private static bool LoadDarkTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\PcTemp"))
                {
                    object value = key == null ? null : key.GetValue("DarkTheme");
                    if (value != null)
                        return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                    object legacyMode = key == null ? null : key.GetValue("ThemeMode");
                    return legacyMode == null || Convert.ToInt32(legacyMode, CultureInfo.InvariantCulture) != 0;
                }
            }
            catch { return true; }
        }

        private static void SaveDarkTheme(bool darkTheme)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\PcTemp"))
            {
                key.SetValue("ThemeMode", darkTheme ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("DarkTheme", darkTheme ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        private void SelectTheme(bool darkTheme)
        {
            _darkTheme = darkTheme;
            _themeMenuItem.Checked = !darkTheme;
            _darkThemeMenuItem.Checked = darkTheme;
            SaveDarkTheme(darkTheme);
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            Color window = _darkTheme ? Color.FromArgb(38, 40, 45) : Color.FromArgb(229, 233, 239);
            Color card = _darkTheme ? Color.FromArgb(27, 29, 34) : Color.White;
            Color text = _darkTheme ? Color.FromArgb(245, 246, 248) : Color.FromArgb(25, 28, 34);
            Color secondary = _darkTheme ? Color.FromArgb(155, 160, 172) : Color.FromArgb(91, 98, 112);
            Color divider = _darkTheme ? Color.FromArgb(48, 50, 57) : Color.FromArgb(222, 225, 231);
            Color menuBackground = _darkTheme ? Color.FromArgb(32, 32, 32) : window;

            BackColor = window;
            BackgroundImage = null;
            ForeColor = text;
            _windowChrome.BackColor = menuBackground;
            _contentPanel.BackColor = window;
            _contentPanel.BackgroundImage = null;
            _titleBar.BackColor = menuBackground;
            _titleCaption.BackColor = menuBackground;
            _titleCaption.ForeColor = text;
            Color captionHover = _darkTheme ? Color.FromArgb(58, 58, 62) : Color.FromArgb(225, 227, 232);
            foreach (Button captionButton in new[] { _minimizeButton, _maximizeButton, _closeButton })
            {
                captionButton.BackColor = menuBackground;
                captionButton.ForeColor = text;
                captionButton.FlatAppearance.MouseDownBackColor = captionHover;
                captionButton.FlatAppearance.MouseOverBackColor = captionHover;
            }
            _closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(196, 43, 28);
            _closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(150, 30, 20);
            _cardsFlow.BackColor = window;
            _verticalScrollBar.BackColor = window;
            _verticalScrollBar.ThumbColor = _darkTheme
                ? Color.FromArgb(118, 123, 132)
                : Color.FromArgb(125, 132, 143);
            _cardsFlow.BackgroundImage = null;
            _mainMenu.BackColor = menuBackground;
            _mainMenu.ForeColor = text;
            _mainMenu.Renderer = new PcTempMenuRenderer(_darkTheme);
            foreach (ToolStripItem item in _mainMenu.Items)
            {
                item.BackColor = menuBackground;
                item.ForeColor = text;
            }
            ApplyMenuTheme(_optionsMenu.DropDownItems, menuBackground, text);
            ApplyMenuTheme(_helpMenu.DropDownItems, menuBackground, text);
            _bottomSpacer.BackColor = window;

            foreach (Control cardControl in _cardsFlow.Controls)
            {
                if (!IsSensorCard(cardControl))
                {
                    ApplyGroupHeaderTheme(cardControl as Panel, window, text, secondary, divider);
                    continue;
                }
                cardControl.BackColor = card;
                SensorCardPanel sensorCard = cardControl as SensorCardPanel;
                Control value = cardControl.Controls["CardValue"];
                Control sensor = cardControl.Controls["CardSensor"];
                Control unit = cardControl.Controls["CardUnit"];
                Control trayLabel = cardControl.Controls["TrayLabel"];
                Control status = cardControl.Controls["CardStatus"];
                Control dividerControl = cardControl.Controls["CardDivider"];
                Control accentLine = cardControl.Controls["CardAccentLine"];
                AccentTile iconTile = cardControl.Controls["CardIconTile"] as AccentTile;
                Control icon = iconTile == null ? cardControl.Controls["CardIcon"] : iconTile.Controls["CardIcon"];
                Control title = cardControl.Controls["CardTitle"];
                Control subtitle = cardControl.Controls["CardSubtitle"];
                Button colorButton = cardControl.Controls["ColorButton"] as Button;
                Button infoButton = cardControl.Controls["InfoButton"] as Button;
                ToggleSwitch toggle = cardControl.Controls["TrayToggle"] as ToggleSwitch;
                TemperatureSparkline graph = cardControl.Controls["Sparkline"] as TemperatureSparkline;
                if (value != null) value.ForeColor = text;
                if (sensor != null) sensor.ForeColor = secondary;
                if (unit != null) unit.ForeColor = secondary;
                if (trayLabel != null) trayLabel.ForeColor = text;
                if (dividerControl != null) dividerControl.BackColor = divider;
                Color displayAccent = colorButton == null ? Color.DodgerBlue :
                    EnsureAccentContrast(colorButton.BackColor, _darkTheme);
                if (sensorCard != null)
                {
                    sensorCard.DarkTheme = _darkTheme;
                    sensorCard.AccentColor = displayAccent;
                }
                if (accentLine != null) accentLine.BackColor = displayAccent;
                if (icon != null) icon.ForeColor = displayAccent;
                if (iconTile != null)
                {
                    iconTile.AccentColor = displayAccent;
                    iconTile.DarkTheme = _darkTheme;
                }
                if (title != null) title.ForeColor = text;
                if (subtitle != null) subtitle.ForeColor = secondary;
                if (infoButton != null)
                {
                    infoButton.ForeColor = secondary;
                    infoButton.BackColor = card;
                    infoButton.FlatAppearance.MouseOverBackColor = _darkTheme
                        ? Color.FromArgb(48, 51, 58) : Color.FromArgb(235, 238, 243);
                    infoButton.FlatAppearance.MouseDownBackColor = _darkTheme
                        ? Color.FromArgb(59, 62, 70) : Color.FromArgb(224, 228, 235);
                }
                if (toggle != null) toggle.AccentColor = displayAccent;
                if (graph != null)
                {
                    graph.DarkTheme = _darkTheme;
                    graph.AccentColor = displayAccent;
                }
                LayoutCardContents(cardControl as Panel);
            }

            ApplyTitleBarTheme();
            RefreshCustomFrame();
            _windowChrome.BringToFront();
            LayoutResizeGrips();
            Invalidate(true);
            Update();
        }

        private static void ApplyGroupHeaderTheme(Panel header, Color background, Color text,
            Color secondary, Color divider)
        {
            if (header == null) return;
            header.BackColor = background;
            Label title = header.Controls["GroupTitle"] as Label;
            Label summary = header.Controls["GroupSummary"] as Label;
            Button button = header.Controls["GroupToggle"] as Button;
            Control line = header.Controls["GroupDivider"];
            if (title != null) title.ForeColor = text;
            if (summary != null) summary.ForeColor = secondary;
            if (button != null)
            {
                button.ForeColor = text;
                button.BackColor = background;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, text);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(45, text);
            }
            if (line != null) line.BackColor = divider;
        }

        private static void ApplyMenuTheme(ToolStripItemCollection items, Color background, Color foreground)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = background;
                item.ForeColor = foreground;
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.DropDownItems.Count > 0)
                    ApplyMenuTheme(menuItem.DropDownItems, background, foreground);
            }
        }

        private void ApplyTitleBarTheme()
        {
            if (!IsHandleCreated) return;
            try
            {
                int dark = _darkTheme ? 1 : 0;
                // The main window uses a fully custom title bar. Applying the
                // Explorer theme to its handle can make Windows paint a second,
                // native frame after switching between light and dark themes.
                if (_contentPanel != null && _contentPanel.IsHandleCreated)
                    _contentPanel.Invalidate(true);
                if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
                ApplyWindowCornerPreference();
                int borderColor = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(Handle, 34, ref borderColor, sizeof(int));
            }
            catch { }
        }

        private void RefreshCustomFrame()
        {
            if (!IsHandleCreated) return;
            PerformLayout();
            ReflowLayout();
            RefreshMenuLayout();
            Invalidate(true);
        }

        private void ApplyWindowCornerPreference()
        {
            if (!IsHandleCreated) return;
            try
            {
                int cornerPreference = WindowState == FormWindowState.Maximized ? 1 : 2;
                DwmSetWindowAttribute(Handle, 33, ref cornerPreference, sizeof(int));
            }
            catch { }
        }

        private void KeepWindowOnScreen()
        {
            if (!IsHandleCreated || WindowState != FormWindowState.Normal) return;

            Rectangle working = Screen.FromRectangle(Bounds).WorkingArea;
            if (working.Width <= 0 || working.Height <= 0) return;

            int width = Math.Min(Math.Max(Width, MinimumSize.Width), working.Width);
            int height = Math.Min(Math.Max(Height, MinimumSize.Height), working.Height);
            int x = Math.Max(working.Left, Math.Min(Left, working.Right - width));
            int y = Math.Max(working.Top, Math.Min(Top, working.Bottom - height));
            Rectangle visibleBounds = new Rectangle(x, y, width, height);
            if (Bounds != visibleBounds)
                Bounds = visibleBounds;
        }

        private void RefreshMenuLayout()
        {
            if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;
            _windowChrome.Width = ClientSize.Width;
            _mainMenu.Width = _windowChrome.ClientSize.Width;
            LayoutCaptionControls();
            _mainMenu.Invalidate(true);
        }

        private Panel CreateGroupHeader(string title, string groupId, EventHandler toggle)
        {
            Color text = _darkTheme ? Color.FromArgb(245, 246, 248) : Color.FromArgb(25, 28, 34);
            Color secondary = _darkTheme ? Color.FromArgb(155, 160, 172) : Color.FromArgb(91, 98, 112);
            Panel header = new Panel
            {
                Height = GroupHeaderHeight,
                Width = MinCardWidth,
                Margin = new Padding(0, 8, 0, 6),
                BackColor = BackColor,
                Tag = groupId,
                TabStop = false
            };
            Label name = new Label
            {
                Name = "GroupTitle",
                Text = title,
                Font = new Font("Segoe UI Semibold", 10F),
                ForeColor = text,
                AutoSize = true,
                Location = new Point(4, 10)
            };
            Label summary = new Label
            {
                Name = "GroupSummary",
                Text = "",
                Font = CardSubtitleFont,
                ForeColor = secondary,
                AutoSize = true,
                Location = new Point(150, 12),
                Visible = false
            };
            Button button = new Button
            {
                Name = "GroupToggle",
                Text = "⌃  Plegar",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = text,
                BackColor = BackColor,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(112, 32),
                Location = new Point(0, 5),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                TabStop = true
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += toggle;
            Panel divider = new Panel
            {
                Name = "GroupDivider",
                BackColor = _darkTheme ? Color.FromArgb(62, 65, 73) : Color.FromArgb(205, 211, 220),
                Height = 1,
                Dock = DockStyle.Bottom
            };
            header.Controls.Add(name);
            header.Controls.Add(summary);
            header.Controls.Add(button);
            header.Controls.Add(divider);
            header.Resize += delegate
            {
                button.Left = Math.Max(0, header.ClientSize.Width - button.Width - 2);
                int summaryRight = button.Left - 12;
                summary.MaximumSize = new Size(Math.Max(0, summaryRight - summary.Left), 22);
            };
            header.MouseWheel += ContentMouseWheel;
            return header;
        }

        private static bool LoadGroupCollapsed(string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\PcTemp"))
                    return Convert.ToInt32(key == null ? 0 : key.GetValue(valueName, 0),
                        CultureInfo.InvariantCulture) != 0;
            }
            catch { return false; }
        }

        private static void SaveGroupCollapsed(string valueName, bool collapsed)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\PcTemp"))
                key.SetValue(valueName, collapsed ? 1 : 0, RegistryValueKind.DWord);
        }

        private void ApplyGroupVisibility()
        {
            bool layoutChanged = _memoryCards.Count != _lastMemoryGroupCount ||
                _diskCards.Count != _lastDiskGroupCount ||
                _memoryGroupCollapsed != _lastMemoryGroupCollapsed ||
                _diskGroupCollapsed != _lastDiskGroupCollapsed;
            _memoryGroupHeader.Visible = _memoryCards.Count > 0;
            _diskGroupHeader.Visible = _diskCards.Count > 0;
            foreach (SensorCardView view in _memoryCards.Values)
                view.Panel.Visible = !_memoryGroupCollapsed;
            foreach (SensorCardView view in _diskCards.Values)
                view.Panel.Visible = !_diskGroupCollapsed;
            UpdateGroupHeader(_memoryGroupHeader, "Memoria RAM", _memoryCards.Values,
                _memoryGroupCollapsed);
            UpdateGroupHeader(_diskGroupHeader, "Discos", _diskCards.Values,
                _diskGroupCollapsed);
            _lastMemoryGroupCount = _memoryCards.Count;
            _lastDiskGroupCount = _diskCards.Count;
            _lastMemoryGroupCollapsed = _memoryGroupCollapsed;
            _lastDiskGroupCollapsed = _diskGroupCollapsed;
            if (layoutChanged)
            {
                // Dynamic sensors can arrive in different batches. Enforce the final
                // category order only after both RAM and disk collections are known.
                EnsureGroupedOrder();
                ReflowLayout();
            }
        }

        private static void UpdateGroupHeader(Panel header, string title,
            IEnumerable<SensorCardView> cards, bool collapsed)
        {
            List<SensorCardView> items = cards.ToList();
            Label name = header.Controls["GroupTitle"] as Label;
            Label summary = header.Controls["GroupSummary"] as Label;
            Button button = header.Controls["GroupToggle"] as Button;
            if (name != null) name.Text = title + " (" + items.Count.ToString(CultureInfo.CurrentCulture) + ")";
            if (summary != null)
            {
                summary.Text = string.Join("  ·  ", items.Select(item =>
                    (string.IsNullOrWhiteSpace(item.Value.Text) ? "--" : item.Value.Text) + "°"));
                summary.Visible = collapsed && items.Count > 0;
            }
            if (button != null) button.Text = collapsed ? "⌄  Expandir" : "⌃  Plegar";
        }

        private Panel CreateCard(string title, string subtitle, Color accent, Label value, Label sensor, string selectionId)
        {
            Color savedColor = LoadCardColor(selectionId, accent);
            Color cardColor = EnsureAccentContrast(savedColor, _darkTheme);
            Color cardBackground = _darkTheme ? Color.FromArgb(27, 29, 34) : Color.White;
            Color primaryText = _darkTheme ? Color.FromArgb(245, 246, 248) : Color.FromArgb(25, 28, 34);
            Color secondaryText = _darkTheme ? Color.FromArgb(155, 160, 172) : Color.FromArgb(91, 98, 112);
            int cardWidth = CalculateCardWidth(title, subtitle, CardTitleFont, CardSubtitleFont);
            SensorCardPanel panel = new SensorCardPanel
            {
                Location = new Point(0, 87),
                Size = new Size(cardWidth, CardHeight),
                BackColor = cardBackground,
                AccentColor = cardColor,
                DarkTheme = _darkTheme,
                Tag = selectionId
            };
            Panel line = new Panel { Name = "CardAccentLine", Height = 1, Visible = false, BackColor = cardColor };
            AccentTile iconTile = new AccentTile
            {
                Name = "CardIconTile",
                AccentColor = cardColor,
                DarkTheme = _darkTheme,
                Location = new Point(18, 14),
                Size = new Size(40, 40)
            };
            HardwareIcon componentIcon = new HardwareIcon
            {
                Name = "CardIcon",
                Kind = GetComponentIconKind(selectionId),
                ForeColor = cardColor,
                Location = new Point(6, 6),
                Size = new Size(28, 28)
            };
            iconTile.Controls.Add(componentIcon);
            Label name = new Label
            {
                Name = "CardTitle",
                Text = title,
                ForeColor = primaryText,
                Font = CardTitleFont,
                AutoSize = false,
                AutoEllipsis = false,
                Size = new Size(cardWidth - 74, 20),
                Location = new Point(68, 14),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Label subtitleLabel = new Label
            {
                Name = "CardSubtitle",
                Text = subtitle,
                ForeColor = secondaryText,
                Font = CardSubtitleFont,
                AutoSize = false,
                AutoEllipsis = true,
                Size = new Size(cardWidth - 74, 20),
                Location = new Point(68, 34),
                TextAlign = ContentAlignment.MiddleLeft
            };
            InfoIconButton infoButton = new InfoIconButton
            {
                Name = "InfoButton",
                AccessibleName = "Información del dispositivo",
                ForeColor = secondaryText,
                BackColor = cardBackground,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(32, 32),
                Location = new Point(cardWidth - 48, 12),
                Cursor = Cursors.Hand,
                TabStop = true,
                UseVisualStyleBackColor = false
            };
            infoButton.FlatAppearance.BorderSize = 0;
            infoButton.FlatAppearance.MouseOverBackColor = _darkTheme
                ? Color.FromArgb(48, 51, 58) : Color.FromArgb(235, 238, 243);
            infoButton.FlatAppearance.MouseDownBackColor = _darkTheme
                ? Color.FromArgb(59, 62, 70) : Color.FromArgb(224, 228, 235);
            _cardToolTip.SetToolTip(infoButton, "Información del dispositivo");
            value.Name = "CardValue";
            value.Text = "--";
            value.ForeColor = primaryText;
            value.Font = CardValueFont;
            value.UseCompatibleTextRendering = false;
            value.AutoSize = !(value is DigitalValueLabel);
            value.Location = new Point(20, 65);
            Label unit = new Label
            {
                Name = "CardUnit",
                Text = "°C",
                ForeColor = secondaryText,
                Font = CardUnitFont,
                AutoSize = true,
                Location = new Point(120, 91),
                Visible = false
            };
            Label status = new Label
            {
                Name = "CardStatus",
                Text = "— estable",
                ForeColor = Color.FromArgb(72, 181, 199),
                Font = CardStatusFont,
                AutoSize = true,
                Location = new Point(188, 107)
            };
            status.TextChanged += delegate { LayoutCardContents(panel); };
            TemperatureSparkline graph = new TemperatureSparkline
            {
                Name = "Sparkline",
                AccentColor = cardColor,
                DarkTheme = _darkTheme,
                StatusTarget = status,
                Location = new Point(18, 137),
                Size = new Size(234, 76)
            };
            sensor.Name = "CardSensor";
            sensor.Text = "Buscando sensor…";
            sensor.ForeColor = secondaryText;
            sensor.Font = CardSensorFont;
            sensor.AutoEllipsis = false;
            sensor.Location = Point.Empty;
            sensor.Size = Size.Empty;
            sensor.Visible = false;
            Panel divider = new Panel
            {
                Name = "CardDivider",
                BackColor = _darkTheme ? Color.FromArgb(48, 50, 57) : Color.FromArgb(222, 225, 231),
                Location = new Point(20, 224),
                Size = new Size(cardWidth - 40, 1)
            };
            ToggleSwitch tray = new ToggleSwitch
            {
                Name = "TrayToggle",
                Location = new Point(20, 239),
                Size = new Size(52, 27),
                AccentColor = cardColor,
                Checked = _isTraySelected == null || _isTraySelected(selectionId),
                Tag = selectionId
            };
            tray.CheckedChanged += delegate
            {
                if (!_updatingTrayToggles && _setTraySelected != null)
                    _setTraySelected((string)tray.Tag, tray.Checked);
            };
            Label trayLabel = new Label
            {
                Name = "TrayLabel",
                Text = "Mostrar en bandeja",
                ForeColor = primaryText,
                Font = CardTrayFont,
                AutoSize = true,
                Location = new Point(82, 243),
                Cursor = Cursors.Hand
            };
            trayLabel.Click += delegate { tray.Checked = !tray.Checked; };
            ColorSwatchButton colorButton = new ColorSwatchButton
            {
                Name = "ColorButton",
                BackColor = savedColor,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(29, 29),
                Location = new Point(cardWidth - 48, 237),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            colorButton.Click += delegate
            {
                using (ThemedColorDialog dialog = new ThemedColorDialog(colorButton.BackColor, _darkTheme))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        Color selectedColor = dialog.SelectedColor;
                        Color displayColor = EnsureAccentContrast(selectedColor, _darkTheme);
                        panel.AccentColor = displayColor;
                        line.BackColor = displayColor;
                        iconTile.AccentColor = displayColor;
                        componentIcon.ForeColor = displayColor;
                        graph.AccentColor = displayColor;
                        tray.AccentColor = displayColor;
                        colorButton.BackColor = selectedColor;
                        SaveCardColor(selectionId, selectedColor);
                        if (_setCardColor != null) _setCardColor(selectionId, selectedColor);
                    }
                }
            };
            panel.Controls.Add(line);
            panel.Controls.Add(iconTile);
            panel.Controls.Add(name);
            panel.Controls.Add(subtitleLabel);
            panel.Controls.Add(infoButton);
            panel.Controls.Add(value);
            panel.Controls.Add(unit);
            panel.Controls.Add(status);
            panel.Controls.Add(graph);
            panel.Controls.Add(sensor);
            panel.Controls.Add(divider);
            panel.Controls.Add(tray);
            panel.Controls.Add(trayLabel);
            panel.Controls.Add(colorButton);
            infoButton.Click += delegate
            {
                try { ShowCardInformation(panel, infoButton); }
                catch (Exception ex)
                {
                    Debug.WriteLine("No se pudo mostrar la información de la tarjeta: " + ex);
                }
            };
            value.TextChanged += delegate
            {
                unit.Visible = value.Text != "--";
                LayoutCardContents(panel);
            };
            LayoutCardContents(panel);
            EnableCardDragging(panel, line, iconTile, componentIcon, name, subtitleLabel, value, unit, status, graph, sensor, divider);
            return panel;
        }

        private void ShowCardInformation(Panel panel, Button source)
        {
            if (panel == null || source == null) return;
            Label title = panel.Controls["CardTitle"] as Label;
            Label subtitle = panel.Controls["CardSubtitle"] as Label;
            Label value = panel.Controls["CardValue"] as Label;
            Label sensor = panel.Controls["CardSensor"] as Label;
            Label status = panel.Controls["CardStatus"] as Label;
            TemperatureSparkline graph = panel.Controls["Sparkline"] as TemperatureSparkline;
            string selectionId = panel.Tag as string ?? "";
            HardwareIconKind kind = GetComponentIconKind(selectionId);
            string cardTitle = title == null ? "Dispositivo" : title.Text;
            string cardSubtitle = subtitle == null ? "" : subtitle.Text;
            SensorCardPanel sensorPanel = panel as SensorCardPanel;
            List<KeyValuePair<string, string>> details = BuildDeviceDetails(kind, cardTitle,
                cardSubtitle, sensor == null ? null : sensor.Text,
                sensor == null ? null : sensor.Tag as string,
                sensorPanel == null ? null : sensorPanel.DeviceDetails);
            string current = value == null || value.Text == "--"
                ? "No disponible" : value.Text + " °C";
            string minimum = graph != null && graph.MinimumValue.HasValue
                ? FormatInfoTemperature(graph.MinimumValue.Value) : "No disponible";
            string maximum = graph != null && graph.MaximumValue.HasValue
                ? FormatInfoTemperature(graph.MaximumValue.Value) : "No disponible";
            string state = status == null ? "Estado no disponible" : status.Text;
            Color accent = sensorPanel == null ? Color.DodgerBlue : sensorPanel.AccentColor;
            using (DeviceInfoDialog dialog = new DeviceInfoDialog(this, _darkTheme, kind, accent,
                cardTitle, cardSubtitle, current, minimum, maximum, state, details))
                dialog.ShowDialog(this);
        }

        private static string SafeInfoText(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? "No disponible" : text.Trim();
        }

        private static List<KeyValuePair<string, string>> BuildDeviceDetails(HardwareIconKind kind,
            string title, string subtitle, string deviceName, string sensorName,
            IList<KeyValuePair<string, string>> extraDetails)
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            if (kind == HardwareIconKind.Cpu)
            {
                result.Add(new KeyValuePair<string, string>("Procesador", SafeInfoText(deviceName)));
                result.Add(new KeyValuePair<string, string>("Fabricante", SafeInfoText(subtitle)));
            }
            else if (kind == HardwareIconKind.Gpu)
            {
                result.Add(new KeyValuePair<string, string>("Tarjeta gráfica", SafeInfoText(deviceName)));
                result.Add(new KeyValuePair<string, string>("Fabricante", SafeInfoText(subtitle)));
            }
            else if (kind == HardwareIconKind.Board)
            {
                result.Add(new KeyValuePair<string, string>("Placa base", SafeInfoText(deviceName)));
                result.Add(new KeyValuePair<string, string>("Fabricante", SafeInfoText(subtitle)));
            }
            else if (kind == HardwareIconKind.Memory)
            {
                result.Add(new KeyValuePair<string, string>("Módulo", SafeInfoText(CleanMemoryIdentifier(deviceName))));
                AppendDeviceDetails(result, extraDetails);
            }
            else
            {
                result.Add(new KeyValuePair<string, string>("Modelo", SafeInfoText(deviceName)));
                AppendDeviceDetails(result, extraDetails);
            }
            result.Add(new KeyValuePair<string, string>("Sensor utilizado", SafeInfoText(
                kind == HardwareIconKind.Memory ? CleanMemoryIdentifier(sensorName) : sensorName)));
            return result;
        }

        private static void AppendDeviceDetails(List<KeyValuePair<string, string>> target,
            IList<KeyValuePair<string, string>> details)
        {
            if (details == null) return;
            foreach (KeyValuePair<string, string> detail in details)
                target.Add(detail);
        }

        private static string CleanMemoryIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            string result = value;
            int marker = result.IndexOf("(#", StringComparison.Ordinal);
            while (marker >= 0)
            {
                int end = result.IndexOf(')', marker + 2);
                if (end < 0) break;
                string number = result.Substring(marker + 2, end - marker - 2);
                if (!number.All(char.IsDigit)) break;
                int removeAt = marker > 0 && result[marker - 1] == ' ' ? marker - 1 : marker;
                result = result.Remove(removeAt, end - removeAt + 1).Trim();
                marker = result.IndexOf("(#", StringComparison.Ordinal);
            }
            return result.Replace("  ·", " ·").Replace("·  ", "· ");
        }

        private static string FormatInfoTemperature(float value)
        {
            return Math.Round(value).ToString("0", CultureInfo.CurrentCulture) + " °C";
        }

        public void SetTraySelectionState(string id, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            Control card = _cardsFlow.Controls.Cast<Control>()
                .FirstOrDefault(control => string.Equals(control.Tag as string, id, StringComparison.OrdinalIgnoreCase));
            ToggleSwitch toggle = card == null ? null : card.Controls["TrayToggle"] as ToggleSwitch;
            if (toggle == null || toggle.Checked == enabled) return;
            _updatingTrayToggles = true;
            try { toggle.Checked = enabled; }
            finally { _updatingTrayToggles = false; }
        }

        private static HardwareIconKind GetComponentIconKind(string selectionId)
        {
            if (string.Equals(selectionId, "cpu", StringComparison.OrdinalIgnoreCase)) return HardwareIconKind.Cpu;
            if (string.Equals(selectionId, "gpu", StringComparison.OrdinalIgnoreCase)) return HardwareIconKind.Gpu;
            if (string.Equals(selectionId, "board", StringComparison.OrdinalIgnoreCase)) return HardwareIconKind.Board;
            if (selectionId != null && selectionId.StartsWith("memory:", StringComparison.OrdinalIgnoreCase)) return HardwareIconKind.Memory;
            return HardwareIconKind.Disk;
        }

        private static int CalculateCardWidth(string title, string subtitle, Font titleFont, Font subtitleFont)
        {
            int titleWidth = TextRenderer.MeasureText(title ?? "", titleFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            int subtitleWidth = TextRenderer.MeasureText(subtitle ?? "", subtitleFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            return Math.Max(MinCardWidth, Math.Min(MaxCardWidth, Math.Max(titleWidth, subtitleWidth) + 82));
        }

        private bool AdjustCardHeader(Panel panel, string title, string subtitle)
        {
            if (panel == null) return false;
            Label name = panel.Controls["CardTitle"] as Label;
            Label subtitleLabel = panel.Controls["CardSubtitle"] as Label;
            if (name == null || subtitleLabel == null) return false;
            bool textChanged = !string.Equals(name.Text, title, StringComparison.Ordinal) ||
                !string.Equals(subtitleLabel.Text, subtitle, StringComparison.Ordinal);
            if (!textChanged) return false;
            name.Text = title;
            subtitleLabel.Text = subtitle;
            int width = CalculateCardWidth(title, subtitle, name.Font, subtitleLabel.Font);
            if (panel.Width != width)
            {
                panel.Width = width;
                LayoutCardContents(panel);
                return true;
            }
            return false;
        }

        private static void LayoutCardContents(Panel panel)
        {
            if (panel == null) return;
            int innerWidth = panel.ClientSize.Width;
            Control name = panel.Controls["CardTitle"];
            Control subtitle = panel.Controls["CardSubtitle"];
            Control graph = panel.Controls["Sparkline"];
            Control sensor = panel.Controls["CardSensor"];
            Control divider = panel.Controls["CardDivider"];
            Control color = panel.Controls["ColorButton"];
            Control status = panel.Controls["CardStatus"];
            Control unit = panel.Controls["CardUnit"];
            Control info = panel.Controls["InfoButton"];
            if (info != null) info.Left = innerWidth - info.Width - 16;
            int headerRight = info == null ? innerWidth - 18 : info.Left - 8;
            if (name != null) name.Width = Math.Max(80, headerRight - name.Left);
            if (subtitle != null) subtitle.Width = Math.Max(80, headerRight - subtitle.Left);
            if (graph != null) graph.Width = Math.Max(220, innerWidth - 36);
            if (sensor != null) sensor.Width = Math.Max(220, innerWidth - 40);
            if (divider != null) divider.Width = Math.Max(1, innerWidth - 40);
            if (color != null) color.Left = innerWidth - color.Width - 19;
            if (status != null) status.Left = Math.Max(150, innerWidth - status.Width - 20);
            if (unit != null && panel.Controls["CardValue"] != null)
            {
                Control value = panel.Controls["CardValue"];
                unit.Left = value.Right + 1;
                unit.Top = value.Top - 7;
            }
        }

        private void EnableCardDragging(Panel panel, params Control[] headerControls)
        {
            panel.Cursor = Cursors.SizeAll;
            panel.MouseDown += CardDragMouseDown;
            panel.MouseMove += CardDragMouseMove;
            foreach (Control control in headerControls)
            {
                control.Cursor = Cursors.SizeAll;
                control.MouseDown += CardDragMouseDown;
                control.MouseMove += CardDragMouseMove;
                control.MouseUp += CardDragMouseUp;
            }
            panel.MouseUp += CardDragMouseUp;
        }

        private void CardDragMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Control control = sender as Control;
            _dragCard = FindCardPanel(control);
            _dragStart = Cursor.Position;
            if (_dragCard != null)
            {
                Point cardScreen = _dragCard.PointToScreen(Point.Empty);
                _dragCardOffset = new Point(Cursor.Position.X - cardScreen.X, Cursor.Position.Y - cardScreen.Y);
            }
            _cardDragStarted = false;
            _dragCaptureControl = control;
            if (_dragCaptureControl != null) _dragCaptureControl.Capture = true;
        }

        private Panel FindCardPanel(Control control)
        {
            while (control != null && control.Parent != _cardsFlow)
                control = control.Parent;
            return control as Panel;
        }

        private void CardDragMouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _dragCard == null) return;
            Size dragSize = System.Windows.Forms.SystemInformation.DragSize;
            if (!_cardDragStarted)
            {
                if (Math.Abs(Cursor.Position.X - _dragStart.X) < dragSize.Width / 2 &&
                    Math.Abs(Cursor.Position.Y - _dragStart.Y) < dragSize.Height / 2) return;
                _cardDragStarted = true;
                ShowDragGhost();
            }
            UpdateDragGhostPosition();
        }

        private void ShowDragGhost()
        {
            CloseDragGhost();
            if (_dragCard == null || _dragCard.Width <= 0 || _dragCard.Height <= 0) return;
            try
            {
                Bitmap preview = new Bitmap(_dragCard.Width, _dragCard.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _dragCard.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                _dragGhost = new CardDragGhost(preview);
                UpdateDragGhostPosition();
                _dragGhost.Show(this);
            }
            catch
            {
                CloseDragGhost();
            }
        }

        private void UpdateDragGhostPosition()
        {
            if (_dragGhost == null) return;
            _dragGhost.Location = new Point(Cursor.Position.X - _dragCardOffset.X,
                Cursor.Position.Y - _dragCardOffset.Y);
        }

        private void CloseDragGhost()
        {
            if (_dragGhost == null) return;
            CardDragGhost ghost = _dragGhost;
            _dragGhost = null;
            try { ghost.Close(); }
            catch { }
            ghost.Dispose();
        }

        private void MoveDraggedCard(Point point)
        {
            if (_dragCard == null || _dragCard.Parent != _cardsFlow) return;
            string draggedGroup = GetControlGroup(_dragCard);
            int oldIndex = _cardsFlow.Controls.GetChildIndex(_dragCard);
            Control target = null;
            double bestDistance = double.MaxValue;
            foreach (Control candidate in _cardsFlow.Controls)
            {
                if (candidate == _dragCard || !IsSensorCard(candidate) ||
                    !string.Equals(GetControlGroup(candidate), draggedGroup, StringComparison.OrdinalIgnoreCase))
                    continue;
                double dx = point.X - (candidate.Left + candidate.Width / 2.0);
                double dy = point.Y - (candidate.Top + candidate.Height / 2.0);
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    target = candidate;
                }
            }
            if (target == null) return;
            int newIndex = _cardsFlow.Controls.GetChildIndex(target);
            bool after = point.Y > target.Top + target.Height / 2 ||
                (point.Y >= target.Top && point.Y <= target.Bottom && point.X > target.Left + target.Width / 2);
            if (after) newIndex++;
            if (newIndex > oldIndex) newIndex--;
            newIndex = Math.Max(0, Math.Min(_cardsFlow.Controls.Count - 1, newIndex));
            if (newIndex == oldIndex) return;
            _cardsFlow.Controls.SetChildIndex(_dragCard, newIndex);
            EnsureGroupedOrder();
            _cardsFlow.PerformLayout();
            _cardsFlow.Invalidate(true);
        }

        private void CardDragMouseUp(object sender, MouseEventArgs e)
        {
            UpdateDragGhostPosition();
            if (_dragCard != null)
            {
                Size dragSize = System.Windows.Forms.SystemInformation.DragSize;
                bool moved = Math.Abs(Cursor.Position.X - _dragStart.X) >= dragSize.Width / 2 ||
                    Math.Abs(Cursor.Position.Y - _dragStart.Y) >= dragSize.Height / 2;
                if (moved)
                {
                    _cardDragStarted = true;
                    MoveDraggedCard(_cardsFlow.PointToClient(Cursor.Position));
                }
            }
            CloseDragGhost();
            if (_dragCaptureControl != null) _dragCaptureControl.Capture = false;
            if (_cardDragStarted)
            {
                SaveCardOrder();
                ReflowLayout();
            }
            _dragCaptureControl = null;
            _dragCard = null;
            _cardDragStarted = false;
        }

        private static string[] LoadCardOrder()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\PcTemp"))
                    return key == null ? new string[0] : (key.GetValue("CardOrder") as string[] ?? new string[0]);
            }
            catch { return new string[0]; }
        }

        private void SaveCardOrder()
        {
            if (_applyingSavedOrder) return;
            string[] order = _cardsFlow.Controls.Cast<Control>()
                .Where(IsSensorCard)
                .Select(control => control.Tag as string)
                .Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\PcTemp"))
                key.SetValue("CardOrder", order, RegistryValueKind.MultiString);
        }

        private void ApplySavedCardOrder()
        {
            string[] saved = LoadCardOrder();
            Dictionary<string, int> rank = saved.Select((id, index) => new { id, index })
                .GroupBy(item => item.id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
            ArrangeGroupedControls(rank);
        }

        private void EnsureGroupedOrder()
        {
            ArrangeGroupedControls(null);
        }

        private void ArrangeGroupedControls(Dictionary<string, int> rank)
        {
            List<Control> controls = _cardsFlow.Controls.Cast<Control>().ToList();
            Func<Control, int> savedRank = control =>
            {
                string id = control.Tag as string;
                int value;
                return rank != null && id != null && rank.TryGetValue(id, out value) ? value : int.MaxValue;
            };
            Func<string, List<Control>> cardsForGroup = group => controls
                .Select((control, index) => new { control, index })
                .Where(item => IsSensorCard(item.control) &&
                    string.Equals(GetControlGroup(item.control), group, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => savedRank(item.control)).ThenBy(item => item.index)
                .Select(item => item.control).ToList();

            List<Control> ordered = new List<Control>();
            ordered.AddRange(cardsForGroup("core"));
            if (_memoryGroupHeader != null) ordered.Add(_memoryGroupHeader);
            ordered.AddRange(cardsForGroup("memory"));
            if (_diskGroupHeader != null) ordered.Add(_diskGroupHeader);
            ordered.AddRange(cardsForGroup("disk"));
            _applyingSavedOrder = true;
            try
            {
                for (int index = 0; index < ordered.Count; index++)
                    _cardsFlow.Controls.SetChildIndex(ordered[index], index);
            }
            finally { _applyingSavedOrder = false; }
        }

        private static bool IsSensorCard(Control control)
        {
            string id = control == null ? null : control.Tag as string;
            return !string.IsNullOrWhiteSpace(id) && !id.StartsWith("group:",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetControlGroup(Control control)
        {
            SensorCardPanel card = control as SensorCardPanel;
            if (card != null && !string.IsNullOrWhiteSpace(card.GroupName))
                return card.GroupName;
            string id = control == null ? null : control.Tag as string;
            if (id != null && id.StartsWith("memory:", StringComparison.OrdinalIgnoreCase)) return "memory";
            if (string.Equals(id, "cpu", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "gpu", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "board", StringComparison.OrdinalIgnoreCase)) return "core";
            return "disk";
        }

        private static Color EnsureAccentContrast(Color color, bool darkTheme)
        {
            double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
            if (!darkTheme && luminance > 0.72)
                return Color.FromArgb((int)(color.R * 0.55), (int)(color.G * 0.55), (int)(color.B * 0.55));
            if (darkTheme && luminance < 0.25)
                return Color.FromArgb(
                    Math.Min(255, (int)(color.R + (255 - color.R) * 0.45)),
                    Math.Min(255, (int)(color.G + (255 - color.G) * 0.45)),
                    Math.Min(255, (int)(color.B + (255 - color.B) * 0.45)));
            return color;
        }

        internal static Color LoadCardColor(string id, Color defaultColor)
        {
            lock (CardColorSync)
            {
                Color cached;
                if (CardColorCache.TryGetValue(id ?? "", out cached)) return cached;
            }

            Color result = defaultColor;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\PcTemp"))
                {
                    object value = key == null ? null : key.GetValue(CardColorValueName(id));
                    if (value != null) result = Color.FromArgb(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                }
            }
            catch { }
            lock (CardColorSync) CardColorCache[id ?? ""] = result;
            return result;
        }

        internal static Color GetDefaultDiskColor(string id)
        {
            return Color.FromArgb(166, 112, 255);
        }

        internal static Color GetDefaultMemoryColor(string id)
        {
            return Color.FromArgb(45, 196, 181);
        }

        private static void SaveCardColor(string id, Color color)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\PcTemp"))
                key.SetValue(CardColorValueName(id), color.ToArgb(), RegistryValueKind.DWord);
            lock (CardColorSync) CardColorCache[id ?? ""] = color;
        }

        private static string CardColorValueName(string id)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(id ?? ""))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "CardColor_" + encoded;
        }

        private void BoardSensorMenuClicked(object sender, EventArgs e)
        {
            ToolStripMenuItem selected = sender as ToolStripMenuItem;
            if (selected == null) return;
            string sensorId = Convert.ToString(selected.Tag) ?? "";
            _autoBoardSensorMenuItem.Checked = sensorId.Length == 0;
            foreach (KeyValuePair<string, ToolStripMenuItem> pair in _boardSensorMenuItems)
                pair.Value.Checked = string.Equals(pair.Key, sensorId, StringComparison.OrdinalIgnoreCase);
            if (_setBoardSensor != null) _setBoardSensor(sensorId);
        }

        private void UpdateBoardSensorMenu(TemperatureSnapshot snapshot)
        {
            HashSet<string> available = new HashSet<string>(
                snapshot.BoardSensors.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
            foreach (string missing in _boardSensorMenuItems.Keys.Where(id => !available.Contains(id)).ToArray())
            {
                ToolStripMenuItem old = _boardSensorMenuItems[missing];
                _boardSensorMenuItem.DropDownItems.Remove(old);
                old.Dispose();
                _boardSensorMenuItems.Remove(missing);
            }

            bool selectedAvailable = string.IsNullOrWhiteSpace(snapshot.BoardSensorModeId) ||
                available.Contains(snapshot.BoardSensorModeId);
            _autoBoardSensorMenuItem.Checked = string.IsNullOrWhiteSpace(snapshot.BoardSensorModeId) ||
                !selectedAvailable;
            _noBoardSensorsMenuItem.Visible = snapshot.BoardSensors.Count == 0;

            foreach (BoardSensorReading reading in snapshot.BoardSensors.OrderByDescending(item => item.Score))
            {
                ToolStripMenuItem item;
                if (!_boardSensorMenuItems.TryGetValue(reading.Id, out item))
                {
                    item = new ToolStripMenuItem { Tag = reading.Id };
                    item.Click += BoardSensorMenuClicked;
                    _boardSensorMenuItems.Add(reading.Id, item);
                    _boardSensorMenuItem.DropDownItems.Add(item);
                }

                item.Text = reading.Name + " · " + Math.Round(reading.Value).ToString("0",
                    CultureInfo.CurrentCulture) + " °C" + (reading.IsStable ? "" : " · lectura filtrada");
                item.ToolTipText = reading.HardwareName + " · " + reading.Name;
                item.Checked = string.Equals(reading.Id, snapshot.BoardSensorModeId,
                    StringComparison.OrdinalIgnoreCase);
            }

            Color menuBackground = _darkTheme ? Color.FromArgb(32, 32, 32) : Color.FromArgb(229, 233, 239);
            Color text = _darkTheme ? Color.FromArgb(245, 246, 248) : Color.FromArgb(25, 28, 34);
            ApplyMenuTheme(_boardSensorMenuItem.DropDownItems, menuBackground, text);
        }

        public void UpdateValues(TemperatureSnapshot snapshot, int seconds, bool startup, bool isAdministrator, bool isPawnIoInstalled)
        {
            Panel gpuCard = _gpuTitle.Parent as Panel;
            bool gpuVisibilityChanged = _gpuAvailable != snapshot.Gpu.HasValue;
            _gpuAvailable = snapshot.Gpu.HasValue;
            if (gpuVisibilityChanged)
                gpuCard.Visible = _gpuAvailable;

            bool cardWidthsChanged = false;
            cardWidthsChanged |= AdjustCardHeader(_cpuTitle.Parent as Panel, "CPU",
                GetHardwareBrand(snapshot.CpuName, "Desconocida"));
            cardWidthsChanged |= AdjustCardHeader(_gpuTitle.Parent as Panel, "GPU",
                GetHardwareBrand(snapshot.GpuName, "Desconocida"));
            cardWidthsChanged |= AdjustCardHeader(_boardTitle.Parent as Panel, "Placa base",
                GetHardwareBrand(snapshot.BoardName, "Desconocida"));
            SetTextIfChanged(_cpuValue, Format(snapshot.Cpu));
            SetTextIfChanged(_gpuValue, Format(snapshot.Gpu));
            SetTextIfChanged(_boardValue, Format(snapshot.Board));
            bool newGraphSample = snapshot.UpdatedAt != default(DateTime) && snapshot.UpdatedAt != _lastGraphSample;
            if (newGraphSample)
            {
                _cpuGraph.AddValue(snapshot.Cpu);
                _gpuGraph.AddValue(snapshot.Gpu);
                _boardGraph.AddValue(snapshot.Board);
                _lastGraphSample = snapshot.UpdatedAt;
            }
            SetTextIfChanged(_cpuSensor, snapshot.CpuName);
            SetTextIfChanged(_gpuSensor, snapshot.GpuName);
            SetTextIfChanged(_boardSensor, snapshot.BoardName);
            _cpuSensor.Tag = snapshot.CpuSensor;
            _gpuSensor.Tag = snapshot.GpuSensor;
            _boardSensor.Tag = snapshot.BoardSensor;
            UpdateBoardSensorMenu(snapshot);
            UpdateDiskCards(snapshot.Disks, newGraphSample);
            UpdateMemoryCards(snapshot.Memories, newGraphSample);
            ApplyGroupVisibility();
            int visibleCardCount = CountVisibleCards();
            bool cardCountChanged = _lastCardCount != visibleCardCount;
            if (cardWidthsChanged || gpuVisibilityChanged || cardCountChanged)
            {
                _lastCardCount = visibleCardCount;
                ApplyAdaptiveWindowSize();
            }
            bool lowLevelMissing = !snapshot.Cpu.HasValue || !snapshot.Board.HasValue;
            bool showDriver = lowLevelMissing && !isPawnIoInstalled;
            bool showElevate = lowLevelMissing && isPawnIoInstalled && !isAdministrator;
            bool actionVisibilityChanged = _driverButton.Visible != showDriver || _elevateButton.Visible != showElevate;
            _driverButton.Visible = showDriver;
            _elevateButton.Visible = showElevate;
            if (actionVisibilityChanged) ReflowLayout();
            if (_startupMenuItem.Checked != startup)
            {
                _updatingStartup = true;
                _startupMenuItem.Checked = startup;
                _updatingStartup = false;
            }
            string updated = snapshot.UpdatedAt == default(DateTime) ? "pendiente" : snapshot.UpdatedAt.ToString("HH:mm:ss");
            string title = "PcTemp " + Program.AppVersion + " · Cada " + seconds + " s · Última actualización: " + updated;
            if (Text != title) Text = title;
        }

        private static void SetTextIfChanged(Control control, string text)
        {
            text = text ?? "";
            if (control.Text != text) control.Text = text;
        }

        private void UpdateDiskCards(List<DiskTemperature> disks, bool addGraphSample)
        {
            while (true)
            {
                string missing = null;
                foreach (string id in _diskCards.Keys)
                {
                    if (!SensorCollection.Contains(disks, id)) { missing = id; break; }
                }
                if (missing == null) break;
                SensorCardView old = _diskCards[missing];
                _cardsFlow.Controls.Remove(old.Panel);
                old.Panel.Dispose();
                _diskCards.Remove(missing);
            }

            bool cardsChanged = false;
            bool cardWidthsChanged = false;
            foreach (DiskTemperature disk in disks)
            {
                SensorCardView view;
                if (!_diskCards.TryGetValue(disk.Id, out view))
                {
                    Label value = new DigitalValueLabel();
                    Label sensor = new Label();
                    string diskTitle = FormatDiskTitle(disk);
                    string diskSubtitle = (string.IsNullOrWhiteSpace(disk.Type) ? "" : disk.Type + " ") + GetDiskBrand(disk.Name);
                    Panel panel = CreateCard(diskTitle, diskSubtitle.Trim(), GetDefaultDiskColor(disk.Id), value, sensor, disk.Id);
                    ((SensorCardPanel)panel).GroupName = "disk";
                    panel.Margin = new Padding(0, 0, 6, 10);
                    view = new SensorCardView
                    {
                        Panel = panel,
                        Value = value,
                        Sensor = sensor,
                        Graph = (TemperatureSparkline)panel.Controls["Sparkline"]
                    };
                    _diskCards.Add(disk.Id, view);
                    _cardsFlow.Controls.Add(panel);
                    cardsChanged = true;
                }

                string currentSubtitle = (string.IsNullOrWhiteSpace(disk.Type) ? "" : disk.Type + " ") + GetDiskBrand(disk.Name);
                cardWidthsChanged |= AdjustCardHeader(view.Panel, FormatDiskTitle(disk), currentSubtitle.Trim());

                SetTextIfChanged(view.Value, Format(disk.Value));
                if (addGraphSample) view.Graph.AddValue(disk.Value);
                SetTextIfChanged(view.Sensor, disk.Name);
                view.Sensor.Tag = disk.Sensor;
                SensorCardPanel diskPanel = view.Panel as SensorCardPanel;
                if (diskPanel != null) diskPanel.DeviceDetails = CreateDiskDeviceDetails(disk);
            }

            if (cardsChanged) ApplySavedCardOrder();
            if (cardWidthsChanged) ReflowLayout();

        }

        private void UpdateMemoryCards(List<MemoryTemperature> memories, bool addGraphSample)
        {
            while (true)
            {
                string missing = null;
                foreach (string id in _memoryCards.Keys)
                {
                    if (!SensorCollection.Contains(memories, id)) { missing = id; break; }
                }
                if (missing == null) break;
                SensorCardView old = _memoryCards[missing];
                _cardsFlow.Controls.Remove(old.Panel);
                old.Panel.Dispose();
                _memoryCards.Remove(missing);
            }

            bool cardsChanged = false;
            foreach (MemoryTemperature memory in memories)
            {
                SensorCardView view;
                if (!_memoryCards.TryGetValue(memory.Id, out view))
                {
                    Label value = new DigitalValueLabel();
                    Label sensor = new Label();
                    string memoryTitle = "RAM" + FormatMemoryCapacity(memory.CapacityBytes);
                    Panel panel = CreateCard(memoryTitle, GetMemoryBrand(memory.Name), GetDefaultMemoryColor(memory.Id), value, sensor, memory.Id);
                    ((SensorCardPanel)panel).GroupName = "memory";
                    panel.Margin = new Padding(0, 0, 6, 10);
                    view = new SensorCardView
                    {
                        Panel = panel,
                        Value = value,
                        Sensor = sensor,
                        Graph = (TemperatureSparkline)panel.Controls["Sparkline"]
                    };
                    _memoryCards.Add(memory.Id, view);
                    _cardsFlow.Controls.Add(panel);
                    cardsChanged = true;
                }

                SetTextIfChanged(view.Value, Format(memory.Value));
                if (addGraphSample) view.Graph.AddValue(memory.Value);
                SetTextIfChanged(view.Sensor, memory.Name);
                view.Sensor.Tag = memory.Sensor;
                SensorCardPanel memoryPanel = view.Panel as SensorCardPanel;
                if (memoryPanel != null) memoryPanel.DeviceDetails = CreateMemoryDeviceDetails(memory);
            }

            if (cardsChanged) ApplySavedCardOrder();

        }

        private static IList<KeyValuePair<string, string>> CreateDiskDeviceDetails(DiskTemperature disk)
        {
            string health = SafeInfoText(disk.Health);
            if (disk.HealthPercent.HasValue)
            {
                string remaining = Math.Round(disk.HealthPercent.Value).ToString("0",
                    CultureInfo.CurrentCulture) + "% restante";
                health = string.Equals(health, "No disponible", StringComparison.OrdinalIgnoreCase)
                    ? remaining : health + " · " + remaining;
            }
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Estado", SafeInfoText(disk.Status)),
                new KeyValuePair<string, string>("Salud", health),
                new KeyValuePair<string, string>("Espacio disponible", FormatFreeSpace(disk.FreeSpaceGigabytes)),
                new KeyValuePair<string, string>("Interfaz", SafeInfoText(disk.Interface)),
                new KeyValuePair<string, string>("Actividad", disk.ActivityPercent.HasValue
                    ? disk.ActivityPercent.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%"
                    : "No disponible"),
                new KeyValuePair<string, string>("Velocidad", FormatDiskSpeed(
                    disk.ReadBytesPerSecond, disk.WriteBytesPerSecond))
            };
        }

        private static IList<KeyValuePair<string, string>> CreateMemoryDeviceDetails(MemoryTemperature memory)
        {
            uint speed = memory.ConfiguredSpeedMHz > 0 ? memory.ConfiguredSpeedMHz : memory.SpeedMHz;
            string voltage = memory.ConfiguredVoltageMillivolts > 0
                ? (memory.ConfiguredVoltageMillivolts / 1000.0).ToString("0.###",
                    CultureInfo.CurrentCulture) + " V" : "No disponible";
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Tipo", SafeInfoText(memory.MemoryType)),
                new KeyValuePair<string, string>("Capacidad", FormatMemoryCapacity(memory.CapacityBytes).Trim()),
                new KeyValuePair<string, string>("Velocidad", speed > 0
                    ? speed.ToString(CultureInfo.CurrentCulture) + " MHz" : "No disponible"),
                new KeyValuePair<string, string>("Ranura", SafeInfoText(memory.Slot)),
                new KeyValuePair<string, string>("Voltaje", voltage)
            };
        }

        private static string FormatFreeSpace(float? gigabytes)
        {
            if (!gigabytes.HasValue) return "No disponible";
            double value = gigabytes.Value;
            if (value >= 1000.0)
                return (value / 1000.0).ToString("0.##", CultureInfo.CurrentCulture) + " TB";
            return value.ToString(value >= 100.0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " GB";
        }

        private static string FormatDiskSpeed(float? readBytes, float? writeBytes)
        {
            if (!readBytes.HasValue && !writeBytes.HasValue) return "No disponible";
            return "L " + FormatByteRate(readBytes) + " · E " + FormatByteRate(writeBytes);
        }

        private static string FormatByteRate(float? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue) return "--";
            double value = Math.Max(0.0, bytesPerSecond.Value);
            if (value >= 1024.0 * 1024.0 * 1024.0)
                return (value / (1024.0 * 1024.0 * 1024.0)).ToString("0.##",
                    CultureInfo.CurrentCulture) + " GB/s";
            if (value >= 1024.0 * 1024.0)
                return (value / (1024.0 * 1024.0)).ToString("0.##",
                    CultureInfo.CurrentCulture) + " MB/s";
            if (value >= 1024.0)
                return (value / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB/s";
            return value.ToString("0", CultureInfo.CurrentCulture) + " B/s";
        }

        private static string GetMemoryBrand(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "módulo";
            string brand = GetDiskBrand(name);
            if (!string.Equals(brand, "desconocido", StringComparison.OrdinalIgnoreCase)) return brand;
            return "módulo";
        }

        private static string FormatDiskCapacity(ulong bytes)
        {
            if (bytes == 0) return "";
            double gigabytes = bytes / 1000000000.0;
            if (gigabytes >= 900.0)
            {
                double terabytes = gigabytes / 1000.0;
                return " " + terabytes.ToString(terabytes >= 10.0 ? "0" : "0.#",
                    CultureInfo.CurrentCulture) + "TB";
            }
            return " " + Math.Round(gigabytes).ToString("0", CultureInfo.CurrentCulture) + "GB";
        }

        private static string FormatDiskTitle(DiskTemperature disk)
        {
            return "Disco" + FormatDiskCapacity(disk.CapacityBytes);
        }

        private static string FormatMemoryCapacity(ulong bytes)
        {
            if (bytes == 0) return "";
            double gigabytes = bytes / 1073741824.0;
            return " " + Math.Round(gigabytes).ToString("0", CultureInfo.CurrentCulture) + "GB";
        }

        private static string GetDiskBrand(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return "desconocido";
            string upper = model.Trim().ToUpperInvariant();
            if (upper.Contains("SAMSUNG")) return "Samsung";
            if (upper.Contains("WESTERN DIGITAL") || upper.StartsWith("WDC ") || upper.StartsWith("WD")) return "Western Digital";
            if (upper.Contains("CRUCIAL") || upper.StartsWith("CT")) return "Crucial";
            if (upper.Contains("KINGSTON")) return "Kingston";
            if (upper.Contains("SEAGATE") || upper.StartsWith("ST")) return "Seagate";
            if (upper.Contains("TOSHIBA")) return "Toshiba";
            if (upper.Contains("KIOXIA")) return "Kioxia";
            if (upper.Contains("SANDISK")) return "SanDisk";
            if (upper.Contains("MICRON")) return "Micron";
            if (upper.Contains("INTEL")) return "Intel";
            if (upper.Contains("SK HYNIX") || upper.Contains("SKHYNIX")) return "SK hynix";
            if (upper.Contains("ADATA")) return "ADATA";
            if (upper.Contains("CORSAIR")) return "Corsair";

            string firstWord = model.Trim().Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrEmpty(firstWord) ? "desconocido" : firstWord;
        }

        private static string GetHardwareBrand(string description, string fallback)
        {
            if (string.IsNullOrWhiteSpace(description)) return fallback;
            string upper = description.Trim().ToUpperInvariant();
            if (upper.Contains("NVIDIA")) return "NVIDIA";
            if (upper.Contains("INTEL")) return "Intel";
            if (upper.Contains("AMD") || upper.Contains("ADVANCED MICRO DEVICES")) return "AMD";
            if (upper.Contains("GIGABYTE") || upper.Contains("AORUS")) return "Gigabyte";
            if (upper.Contains("ASUSTEK") || upper.Contains("ASUS") || upper.Contains("ROG ") || upper.Contains("TUF ")) return "ASUS";
            if (upper.Contains("MICRO-STAR") || upper.StartsWith("MSI ") || upper == "MSI") return "MSI";
            if (upper.Contains("ASROCK")) return "ASRock";
            if (upper.Contains("BIOSTAR")) return "Biostar";
            if (upper.Contains("SUPERMICRO")) return "Supermicro";
            if (upper.Contains("EVGA")) return "EVGA";

            string firstWord = description.Trim().Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrEmpty(firstWord) ? fallback : firstWord;
        }

        internal void OptimizeWindowSize()
        {
            if (WindowState == FormWindowState.Normal)
                ApplyAdaptiveWindowSize();
        }

        private void ApplyAdaptiveWindowSize()
        {
            Rectangle working = Screen.FromControl(this).WorkingArea;
            int maximumWindowWidth = Math.Max(410, working.Width - 16);
            int maximumWindowHeight = Math.Max(430, working.Height - 16);
            int visibleCardCount = Math.Max(1, CountVisibleCards());
            int preferredColumns = visibleCardCount <= 4
                ? visibleCardCount
                : Math.Min(4, (visibleCardCount + 1) / 2);
            int preferredFlowWidth = CalculateRequiredFlowWidth(1);
            for (int columns = preferredColumns; columns >= 1; columns--)
            {
                int requiredFlowWidth = CalculateRequiredFlowWidth(columns);
                if (44 + requiredFlowWidth <= maximumWindowWidth || columns == 1)
                {
                    preferredFlowWidth = requiredFlowWidth;
                    break;
                }
            }
            int desiredWidth = Math.Min(Math.Max(660, 44 + preferredFlowWidth), maximumWindowWidth);
            int flowWidth = Math.Max(MinCardWidth, desiredWidth - 44);
            int totalFlowHeight = CalculateFlowHeight(flowWidth);
            int headerHeight = VisibleGroupHeaderCount() * GroupHeaderStride;
            int preferredVisibleHeight = Math.Min(totalFlowHeight, 2 * CardStrideY + headerHeight);
            int maximumVisibleHeight = Math.Max(CardStrideY,
                maximumWindowHeight - _windowChrome.Height - 18 - AdaptiveBottomSpace);
            int desiredHeight = _windowChrome.Height + 18 +
                Math.Min(preferredVisibleHeight, maximumVisibleHeight) + AdaptiveBottomSpace;
            int width = Math.Min(desiredWidth, maximumWindowWidth);
            int height = Math.Min(Math.Max(420, desiredHeight), maximumWindowHeight);

            _layingOut = true;
            ClientSize = new Size(width, height);
            _layingOut = false;
            ReflowLayout();
            CenterToScreen();
        }

        private int CalculateRequiredFlowWidth(int columns)
        {
            _visibleCardWidths.Clear();
            foreach (Control control in _cardsFlow.Controls)
            {
                if (IsCardIncluded(control))
                    _visibleCardWidths.Add(control.Width + control.Margin.Horizontal);
            }
            if (_visibleCardWidths.Count == 0) return MinCardWidth;
            columns = Math.Max(1, Math.Min(columns, _visibleCardWidths.Count));
            int maximum = 0;
            for (int start = 0; start <= _visibleCardWidths.Count - columns; start++)
            {
                int rowWidth = 0;
                for (int index = 0; index < columns; index++)
                    rowWidth += _visibleCardWidths[start + index];
                maximum = Math.Max(maximum, rowWidth);
            }
            return Math.Max(MinCardWidth, maximum);
        }

        private void ReflowLayout()
        {
            if (_layingOut || _cardsFlow == null) return;
            _layingOut = true;
            try
            {
                _windowChrome.Width = ClientSize.Width;
                _mainMenu.Width = _windowChrome.ClientSize.Width;
                const int ResizeEdgeWidth = 6;
                const int ScrollBarWidth = 18;
                int scrollBarLeft = Math.Max(1, ClientSize.Width - ResizeEdgeWidth - ScrollBarWidth);
                _contentPanel.Bounds = new Rectangle(0, _windowChrome.Bottom,
                    scrollBarLeft, Math.Max(1, ClientSize.Height - _windowChrome.Bottom - ResizeEdgeWidth));
                _verticalScrollBar.Bounds = new Rectangle(scrollBarLeft, _windowChrome.Bottom,
                    ScrollBarWidth, Math.Max(1, ClientSize.Height - _windowChrome.Bottom - ResizeEdgeWidth));
                int contentWidth = Math.Max(MinCardWidth + 44, _contentPanel.ClientSize.Width);
                int availableWidth = Math.Max(MinCardWidth, contentWidth - 20);
                UpdateGroupHeaderWidths(availableWidth);
                AlignCardGroupsLeft();
                _cardsFlow.Size = new Size(availableWidth, CalculateFlowHeight(availableWidth));
                _cardsFlow.PerformLayout();

                int cardsTop = 14;
                int actionsTop = cardsTop + _cardsFlow.Height + 10;
                int actionsWidth = Math.Max(330, contentWidth - 48);
                int actionColumns = actionsWidth >= 350 ? 2 : 1;
                int gap = 12;
                int buttonWidth = (actionsWidth - gap * (actionColumns - 1)) / actionColumns;
                Button firstButton = null;
                Button secondButton = null;
                int buttonCount = 0;
                if (_driverButton.Visible) firstButton = _driverButton;
                if (_elevateButton.Visible)
                {
                    if (firstButton == null) firstButton = _elevateButton;
                    else secondButton = _elevateButton;
                }
                if (firstButton != null) buttonCount++;
                if (secondButton != null) buttonCount++;
                for (int index = 0; index < buttonCount; index++)
                {
                    Button button = index == 0 ? firstButton : secondButton;
                    int row = index / actionColumns;
                    int column = index % actionColumns;
                    button.Size = new Size(buttonWidth, 32);
                }

                int actionRows = (buttonCount + actionColumns - 1) / actionColumns;
                int contentBottom = actionRows > 0 ? actionsTop + actionRows * 42 : cardsTop + _cardsFlow.Height;
                ConfigureVerticalScroll(contentBottom + 54, _contentPanel.ClientSize.Height);
                int scrollOffset = _verticalScrollBar.Visible ? _verticalScrollBar.Value : 0;
                _cardsFlow.Location = new Point(10, cardsTop - scrollOffset);
                for (int index = 0; index < buttonCount; index++)
                {
                    Button button = index == 0 ? firstButton : secondButton;
                    int row = index / actionColumns;
                    int column = index % actionColumns;
                    button.Location = new Point(24 + column * (buttonWidth + gap),
                        actionsTop + row * 42 - scrollOffset);
                }
                _bottomSpacer.Location = new Point(0, contentBottom + 6 - scrollOffset);
                _bottomSpacer.Size = new Size(Math.Max(1, contentWidth - 24), 40);
                _bottomSpacer.SendToBack();
                _verticalScrollBar.BringToFront();
            }
            finally
            {
                _layingOut = false;
            }
        }

        private void ConfigureVerticalScroll(int contentHeight, int viewportHeight)
        {
            bool visible = contentHeight > viewportHeight;
            _verticalScrollBar.Visible = visible;
            if (!visible)
            {
                if (_verticalScrollBar.Value != 0)
                    _verticalScrollBar.Value = 0;
                return;
            }

            _verticalScrollBar.Minimum = 0;
            _verticalScrollBar.LargeChange = Math.Max(1, viewportHeight);
            _verticalScrollBar.SmallChange = Math.Max(30, CardStrideY / 4);
            _verticalScrollBar.Maximum = Math.Max(0, contentHeight - 1);
            int maximumValue = Math.Max(0,
                _verticalScrollBar.Maximum - _verticalScrollBar.LargeChange + 1);
            if (_verticalScrollBar.Value > maximumValue)
                _verticalScrollBar.Value = maximumValue;
        }

        private void ContentMouseWheel(object sender, MouseEventArgs e)
        {
            if (!_verticalScrollBar.Visible || e.Delta == 0) return;
            int maximumValue = Math.Max(0,
                _verticalScrollBar.Maximum - _verticalScrollBar.LargeChange + 1);
            int notches = e.Delta / System.Windows.Forms.SystemInformation.MouseWheelScrollDelta;
            if (notches == 0) notches = Math.Sign(e.Delta);
            int value = _verticalScrollBar.Value - notches * _verticalScrollBar.SmallChange;
            _verticalScrollBar.Value = Math.Max(_verticalScrollBar.Minimum, Math.Min(maximumValue, value));
        }

        private void UpdateGroupHeaderWidths(int availableWidth)
        {
            foreach (Panel header in new[] { _memoryGroupHeader, _diskGroupHeader })
            {
                if (header != null && header.Width != availableWidth)
                    header.Width = availableWidth;
            }
        }

        private void AlignCardGroupsLeft()
        {
            foreach (Control card in _cardsFlow.Controls.Cast<Control>().Where(IsSensorCard))
                card.Margin = new Padding(0, card.Margin.Top, 6, card.Margin.Bottom);
        }

        private int CalculateFlowHeight(int availableWidth)
        {
            int height = CalculateGroupRows("core", availableWidth) * CardStrideY;
            if (_memoryCards.Count > 0)
                height += GroupHeaderStride + CalculateGroupRows("memory", availableWidth) * CardStrideY;
            if (_diskCards.Count > 0)
                height += GroupHeaderStride + CalculateGroupRows("disk", availableWidth) * CardStrideY;
            return Math.Max(1, height);
        }

        private int CalculateGroupRows(string group, int availableWidth)
        {
            int rows = 0;
            int used = 0;
            foreach (Control control in _cardsFlow.Controls)
            {
                if (!IsCardIncluded(control) || !string.Equals(GetControlGroup(control), group,
                    StringComparison.OrdinalIgnoreCase)) continue;
                int outerWidth = control.Width + control.Margin.Horizontal;
                if (used > 0 && used + outerWidth > availableWidth)
                {
                    rows++;
                    used = 0;
                }
                used += outerWidth;
            }
            if (used > 0) rows++;
            return rows;
        }

        private int VisibleGroupHeaderCount()
        {
            int count = 0;
            if (_memoryCards.Count > 0) count++;
            if (_diskCards.Count > 0) count++;
            return count;
        }

        private int CountVisibleCards()
        {
            int count = 0;
            foreach (Control control in _cardsFlow.Controls)
            {
                if (IsCardIncluded(control)) count++;
            }
            return count;
        }

        private bool IsCardIncluded(Control control)
        {
            if (!IsSensorCard(control)) return false;
            string group = GetControlGroup(control);
            if (string.Equals(group, "memory", StringComparison.OrdinalIgnoreCase))
                return !_memoryGroupCollapsed;
            if (string.Equals(group, "disk", StringComparison.OrdinalIgnoreCase))
                return !_diskGroupCollapsed;
            return !string.Equals(control.Tag as string, "gpu", StringComparison.OrdinalIgnoreCase) || _gpuAvailable;
        }

        public void ShowError(string message)
        {
            Text = "PcTemp " + Program.AppVersion + " · Error de lectura de sensores";
        }

        private static string Format(float? value)
        {
            return value.HasValue
                ? Math.Round(value.Value).ToString("0", CultureInfo.CurrentCulture)
                : "--";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseDragGhost();
                if (_titleIcon != null && _titleIcon.Image != null)
                {
                    Image image = _titleIcon.Image;
                    _titleIcon.Image = null;
                    image.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TemperatureSparkline : Control
    {
        private const int MaximumSamples = 60;
        private static readonly Font GraphLabelFont = new Font("Segoe UI Semibold", 9F);
        private readonly float[] _values = new float[MaximumSamples];
        private PointF[] _renderPoints = new PointF[0];
        private PointF[] _renderArea = new PointF[0];
        private int _valueCount;
        private int _nextValueIndex;
        private Color _accentColor;
        private bool _darkTheme = true;

        public Label StatusTarget { get; set; }

        public bool DarkTheme
        {
            get { return _darkTheme; }
            set
            {
                _darkTheme = value;
                BackColor = value ? Color.FromArgb(21, 22, 27) : Color.FromArgb(247, 248, 250);
                Invalidate();
            }
        }

        public Color AccentColor
        {
            get { return _accentColor; }
            set { _accentColor = value; Invalidate(); }
        }

        public TemperatureSparkline()
        {
            AccentColor = Color.DeepSkyBlue;
            DarkTheme = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void AddValue(float? value)
        {
            if (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value)) return;
            _values[_nextValueIndex] = value.Value;
            _nextValueIndex = (_nextValueIndex + 1) % MaximumSamples;
            if (_valueCount < MaximumSamples) _valueCount++;
            UpdateStatus();
            Invalidate();
        }

        private void UpdateStatus()
        {
            if (StatusTarget == null || _valueCount < 2) return;
            float delta = GetValue(_valueCount - 1) - GetValue(_valueCount - 2);
            if (delta > 0.5F)
            {
                StatusTarget.Text = "↗ subiendo";
                StatusTarget.ForeColor = Color.FromArgb(239, 83, 80);
            }
            else if (delta < -0.5F)
            {
                StatusTarget.Text = "↘ bajando";
                StatusTarget.ForeColor = Color.FromArgb(46, 204, 113);
            }
            else
            {
                StatusTarget.Text = "— estable";
                StatusTarget.ForeColor = Color.FromArgb(72, 181, 199);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
            RectangleF backgroundBounds = new RectangleF(0.5F, 0.5F,
                Math.Max(1F, Width - 1.5F), Math.Max(1F, Height - 1.5F));
            using (GraphicsPath backgroundPath = CreateRoundedPath(backgroundBounds, 5F))
            using (SolidBrush background = new SolidBrush(BackColor))
            {
                e.Graphics.FillPath(background, backgroundPath);
            }

            Color gridColor = DarkTheme ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(35, 20, 30, 45);
            const float labelColumnWidth = 48F;
            const float plotLeft = 8F;
            const float plotTop = 8F;
            float plotRight = Math.Max(48F, Width - labelColumnWidth - 7F);
            float plotBottom = Math.Max(plotTop + 8F, Height - 8F);
            using (Pen grid = new Pen(gridColor))
            {
                float firstGrid = plotTop + (plotBottom - plotTop) / 3F;
                float secondGrid = plotTop + (plotBottom - plotTop) * 2F / 3F;
                e.Graphics.DrawLine(grid, plotLeft, firstGrid, plotRight, firstGrid);
                e.Graphics.DrawLine(grid, plotLeft, secondGrid, plotRight, secondGrid);
                e.Graphics.DrawLine(grid, plotRight + 5F, plotTop, plotRight + 5F, plotBottom);
            }

            if (_valueCount == 0) return;
            float observedMinimum = GetValue(0);
            float observedMaximum = observedMinimum;
            for (int index = 1; index < _valueCount; index++)
            {
                float current = GetValue(index);
                if (current < observedMinimum) observedMinimum = current;
                if (current > observedMaximum) observedMaximum = current;
            }

            float minimum = observedMinimum;
            float maximum = observedMaximum;
            if (maximum - minimum < 4F)
            {
                float middle = (maximum + minimum) / 2F;
                minimum = middle - 2F;
                maximum = middle + 2F;
            }

            if (_renderPoints.Length != _valueCount)
                _renderPoints = new PointF[_valueCount];
            for (int index = 0; index < _valueCount; index++)
            {
                float x = _valueCount == 1 ? plotRight - 3F :
                    plotLeft + index * (plotRight - plotLeft - 3F) / (_valueCount - 1F);
                float ratio = (GetValue(index) - minimum) / (maximum - minimum);
                float y = plotBottom - 2F - ratio * (plotBottom - plotTop - 4F);
                _renderPoints[index] = new PointF(x, y);
            }

            if (_renderPoints.Length > 1)
            {
                if (_renderArea.Length != _renderPoints.Length + 2)
                    _renderArea = new PointF[_renderPoints.Length + 2];
                Array.Copy(_renderPoints, _renderArea, _renderPoints.Length);
                _renderArea[_renderArea.Length - 2] = new PointF(_renderPoints[_renderPoints.Length - 1].X, plotBottom);
                _renderArea[_renderArea.Length - 1] = new PointF(_renderPoints[0].X, plotBottom);
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(DarkTheme ? 34 : 22, AccentColor)))
                    e.Graphics.FillPolygon(fill, _renderArea);
                using (Pen line = new Pen(AccentColor, 2.2F))
                {
                    line.StartCap = LineCap.Round;
                    line.EndCap = LineCap.Round;
                    e.Graphics.DrawLines(line, _renderPoints);
                }
            }
            using (Brush dot = new SolidBrush(AccentColor))
            {
                PointF last = _renderPoints[_renderPoints.Length - 1];
                e.Graphics.FillEllipse(dot, last.X - 2F, last.Y - 2F, 4F, 4F);
            }

            Color labelColor = DarkTheme ? Color.FromArgb(115, 125, 145) : Color.FromArgb(105, 112, 126);
            using (Brush labelBrush = new SolidBrush(labelColor))
            using (StringFormat format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            })
            {
                string maxText = Math.Round(observedMaximum).ToString("0", CultureInfo.InvariantCulture) + "°";
                string minText = Math.Round(observedMinimum).ToString("0", CultureInfo.InvariantCulture) + "°";
                RectangleF maxArea = new RectangleF(plotRight + 7F, 2F, labelColumnWidth - 9F, 20F);
                RectangleF minArea = new RectangleF(plotRight + 7F, Height - 22F, labelColumnWidth - 9F, 20F);
                e.Graphics.DrawString(maxText, GraphLabelFont, labelBrush, maxArea, format);
                e.Graphics.DrawString(minText, GraphLabelFont, labelBrush, minArea, format);
            }
        }

        public float? MinimumValue
        {
            get
            {
                if (_valueCount == 0) return null;
                float result = GetValue(0);
                for (int index = 1; index < _valueCount; index++)
                    result = Math.Min(result, GetValue(index));
                return result;
            }
        }

        public float? MaximumValue
        {
            get
            {
                if (_valueCount == 0) return null;
                float result = GetValue(0);
                for (int index = 1; index < _valueCount; index++)
                    result = Math.Max(result, GetValue(index));
                return result;
            }
        }

        private float GetValue(int chronologicalIndex)
        {
            int firstIndex = _valueCount < MaximumSamples ? 0 : _nextValueIndex;
            return _values[(firstIndex + chronologicalIndex) % MaximumSamples];
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            float diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2F);
            GraphicsPath path = new GraphicsPath();
            if (diameter <= 1F)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180F, 90F);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270F, 90F);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0F, 90F);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90F, 90F);
            path.CloseFigure();
            return path;
        }
    }

    internal static class TrayIconFactory
    {
        private struct TextLayout
        {
            public float FontSize;
            public SizeF Size;
        }

        private static readonly object LayoutSync = new object();
        private static readonly Dictionary<string, TextLayout> LayoutCache =
            new Dictionary<string, TextLayout>(StringComparer.Ordinal);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        internal static int PreferredSize
        {
            get
            {
                int systemSize = Math.Max(System.Windows.Forms.SystemInformation.SmallIconSize.Width,
                    System.Windows.Forms.SystemInformation.SmallIconSize.Height);
                return Math.Max(16, Math.Min(64, systemSize));
            }
        }

        public static Icon Create(string text, Color color)
        {
            return CreateAtSize(text, color, PreferredSize);
        }

        internal static Icon CreateAtSize(string text, Color color, int pixelSize)
        {
            pixelSize = Math.Max(16, Math.Min(64, pixelSize));
            const int renderScale = 4;
            int renderSize = pixelSize * renderScale;
            using (Bitmap source = new Bitmap(renderSize, renderSize,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(source))
            using (Brush brush = new SolidBrush(color))
            using (Brush shadow = new SolidBrush(Color.FromArgb(230, 0, 0, 0)))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                TextLayout layout;
                string layoutKey = renderSize.ToString(CultureInfo.InvariantCulture) + "|" + text;
                lock (LayoutSync)
                {
                    if (!LayoutCache.TryGetValue(layoutKey, out layout))
                    {
                        float minimumFontSize = Math.Max(7F, renderSize * 0.30F);
                        layout.FontSize = minimumFontSize;
                        for (float fontSize = renderSize * 0.94F;
                             fontSize >= minimumFontSize; fontSize -= 1F)
                        {
                            using (Font candidate = new Font("Bahnschrift Condensed", fontSize,
                                FontStyle.Bold, GraphicsUnit.Pixel))
                            {
                                SizeF candidateSize = graphics.MeasureString(text, candidate, renderSize * 2,
                                    StringFormat.GenericTypographic);
                                if (candidateSize.Width <= renderSize - renderScale &&
                                    candidateSize.Height <= renderSize - renderScale)
                                {
                                    layout.FontSize = fontSize;
                                    layout.Size = candidateSize;
                                    break;
                                }
                            }
                        }
                        if (layout.Size.IsEmpty)
                        {
                            using (Font fallback = new Font("Bahnschrift Condensed", layout.FontSize,
                                FontStyle.Bold, GraphicsUnit.Pixel))
                                layout.Size = graphics.MeasureString(text, fallback, renderSize * 2,
                                    StringFormat.GenericTypographic);
                        }
                        LayoutCache[layoutKey] = layout;
                    }
                }

                using (Font font = new Font("Bahnschrift Condensed", layout.FontSize,
                    FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    float x = (renderSize - layout.Size.Width) / 2F;
                    float y = (renderSize - layout.Size.Height) / 2F - renderScale * 0.35F;
                    if (pixelSize > 20)
                        graphics.DrawString(text, font, shadow, x + renderScale * 0.4F,
                            y + renderScale * 0.4F, StringFormat.GenericTypographic);
                    graphics.DrawString(text, font, brush, x, y, StringFormat.GenericTypographic);
                }

                using (Bitmap bitmap = new Bitmap(pixelSize, pixelSize,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (Graphics target = Graphics.FromImage(bitmap))
                {
                    target.Clear(Color.Transparent);
                    target.CompositingMode = CompositingMode.SourceCopy;
                    target.CompositingQuality = CompositingQuality.HighQuality;
                    target.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    target.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    target.DrawImage(source, new Rectangle(0, 0, pixelSize, pixelSize),
                        0, 0, renderSize, renderSize, GraphicsUnit.Pixel);
                    IntPtr handle = bitmap.GetHicon();
                    try
                    {
                        using (Icon temporary = Icon.FromHandle(handle))
                            return (Icon)temporary.Clone();
                    }
                    finally
                    {
                        DestroyIcon(handle);
                    }
                }
            }
        }
    }
}
