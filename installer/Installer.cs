using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PcTemp Setup")]
[assembly: AssemblyProduct("PcTemp")]
[assembly: AssemblyCompany("PcTemp")]
[assembly: AssemblyVersion("1.13.65.0")]
[assembly: AssemblyFileVersion("1.13.65.0")]

namespace PcTempInstaller
{
    internal static class Program
    {
        private const string ProductName = "PcTemp";
        internal const string Version = "1.13.65";
        private const string TaskName = "PcTemp";
        private const string LegacyTaskName = "PcTemp_Startup";
        private const string PawnIoRepairTaskName = "PcTemp_PawnIO_Repair";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PcTemp";
        private const string SettingsKey = @"Software\PcTemp";
        private const string InstallMarkerFile = ".pctemp-install";
        private const string PawnIoManagedValue = "PawnIoManagedByPcTemp";
        private const string PawnIoRestartPendingValue = "PawnIoRestartPending";
        private const string PawnIoSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 1 && string.Equals(args[0], "--repair-pawnio", StringComparison.OrdinalIgnoreCase))
            {
                CompletePawnIoRepair(args[1]);
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                BeginUninstall();
                return;
            }

            if (args.Length > 1 && string.Equals(args[0], "--remove", StringComparison.OrdinalIgnoreCase))
            {
                PerformRemoval(args[1]);
                return;
            }

            Application.Run(new SetupForm());
        }

        internal static string DefaultInstallDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName); }
        }

        internal static string RegisteredInstallDirectory
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(UninstallKey))
                    {
                        string path = key == null ? null : Convert.ToString(key.GetValue("InstallLocation"));
                        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
                    }
                }
                catch { return null; }
            }
        }

        internal static string SuggestedInstallDirectory
        {
            get { return RegisteredInstallDirectory ?? DefaultInstallDirectory; }
        }

        internal static bool IsInstalled
        {
            get
            {
                string target = RegisteredInstallDirectory;
                return !string.IsNullOrWhiteSpace(target) && File.Exists(Path.Combine(target, "PcTemp.exe"));
            }
        }

        internal static bool DesktopShortcutExists
        {
            get
            {
                return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PcTemp.lnk")) ||
                       File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "PcTemp.lnk"));
            }
        }

        internal static string ValidateInstallDirectory(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
                throw new InvalidOperationException("Selecciona una carpeta de instalación.");

            string expanded = Environment.ExpandEnvironmentVariables(requestedPath.Trim().Trim('"'));
            string target = Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetPathRoot(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Selecciona una carpeta para PcTemp, no la raíz de la unidad.");

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (target.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("No se puede instalar PcTemp dentro de la carpeta de Windows.");
            return target;
        }

        internal static bool Install(string installDirectory, bool createDesktopShortcut, bool sendErrorReports, Action<string> progress)
        {
            string previousTarget = RegisteredInstallDirectory;
            bool? registeredPawnIoOwnership = GetRegisteredPawnIoOwnership();
            string target = ValidateInstallDirectory(installDirectory);
            bool isRegisteredTarget = !string.IsNullOrWhiteSpace(previousTarget) &&
                string.Equals(Path.GetFullPath(previousTarget), target, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() &&
                !isRegisteredTarget)
                throw new InvalidOperationException("La carpeta elegida no está vacía. Selecciona una carpeta vacía o crea una nueva para PcTemp.");
            Directory.CreateDirectory(target);
            if (progress != null) progress("Preparando la instalación de PcTemp…");
            StopPcTemp();
            ExtractPayload(target);

            if (progress != null) progress("Instalando acceso integrado a sensores PawnIO…");
            bool pawnIoInstalledNow;
            bool pawnIoRepairPending;
            bool rebootRequired = InstallPawnIo(target, out pawnIoInstalledNow, out pawnIoRepairPending);
            bool pawnIoManaged = registeredPawnIoOwnership.HasValue
                ? registeredPawnIoOwnership.Value
                : (!string.IsNullOrWhiteSpace(previousTarget) && IsPawnIoDriverInstalled());
            pawnIoManaged = pawnIoManaged || pawnIoInstalledNow;
            WriteInstallMarker(target, pawnIoManaged);

            string installedSetup = Path.Combine(target, "PcTemp-Setup.exe");
            string currentSetup = Path.GetFullPath(Application.ExecutablePath);
            if (!string.Equals(installedSetup, currentSetup, StringComparison.OrdinalIgnoreCase))
                File.Copy(currentSetup, installedSetup, true);

            if (pawnIoRepairPending)
                CreatePawnIoRepairTask(installedSetup, target);
            else
                DeletePawnIoRepairTask();

            CreateShortcuts(target, installedSetup, createDesktopShortcut);
            CreateStartupTask(Path.Combine(target, "PcTemp.exe"));
            SaveErrorReportConsent(sendErrorReports);
            RegisterUninstaller(target, installedSetup, pawnIoManaged, rebootRequired);
            RemovePreviousInstallation(previousTarget, target);

            if (!rebootRequired)
                Process.Start(new ProcessStartInfo(Path.Combine(target, "PcTemp.exe")) { UseShellExecute = true });
            return rebootRequired;
        }

        private static bool InstallPawnIo(string target, out bool installedByPcTemp, out bool repairAfterRestart)
        {
            installedByPcTemp = false;
            repairAfterRestart = false;
            string installer = Path.Combine(target, "PawnIO_setup.exe");
            if (!File.Exists(installer))
                throw new FileNotFoundException("El paquete no contiene el instalador integrado de PawnIO.", installer);

            string actualHash;
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read))
                actualHash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "");
            if (!string.Equals(actualHash, PawnIoSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La comprobación de seguridad de PawnIO no coincide con la versión oficial 2.2.0.");

            // PawnIO devuelve ERROR_ALREADY_EXISTS (183) cuando su controlador ya
            // está registrado. Evitamos relanzarlo si la instalación existente es válida.
            if (IsPawnIoDriverInstalled()) return false;
            if (TryRestorePawnIoDriver())
            {
                installedByPcTemp = true;
                return false;
            }

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "-install -silent",
                WorkingDirectory = target,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            };
            using (Process process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("No se pudo iniciar la instalación integrada de PawnIO.");
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    installedByPcTemp = true;
                    return false;
                }
                if (process.ExitCode == 3010 || process.ExitCode == 1641)
                {
                    installedByPcTemp = true;
                    return true;
                }
                if (process.ExitCode == 183 && IsPawnIoDriverInstalled()) return false;
                if (process.ExitCode == 183 && HasPawnIoServiceRegistration())
                {
                    MarkPawnIoServiceForRepair();
                    installedByPcTemp = true;
                    repairAfterRestart = true;
                    return true;
                }
                if (process.ExitCode == 1072)
                {
                    // ERROR_SERVICE_MARKED_FOR_DELETE: el controlador anterior sigue
                    // pendiente de eliminación hasta reiniciar Windows.
                    installedByPcTemp = true;
                    repairAfterRestart = true;
                    return true;
                }
                throw new InvalidOperationException("PawnIO no pudo instalarse. Código: " + process.ExitCode + ".");
            }
        }

        private static void CompletePawnIoRepair(string requestedTarget)
        {
            try
            {
                string registered = RegisteredInstallDirectory;
                if (string.IsNullOrWhiteSpace(registered))
                {
                    DeletePawnIoRepairTask();
                    return;
                }

                string target = Path.GetFullPath(requestedTarget).TrimEnd(Path.DirectorySeparatorChar);
                string expected = Path.GetFullPath(registered).TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(target, expected, StringComparison.OrdinalIgnoreCase))
                {
                    DeletePawnIoRepairTask();
                    return;
                }

                bool installedByPcTemp;
                bool repairPending;
                bool rebootRequired = InstallPawnIo(target, out installedByPcTemp, out repairPending);
                SetPawnIoRestartPending(repairPending || rebootRequired);
                if (!repairPending && !rebootRequired) DeletePawnIoRepairTask();
            }
            catch
            {
                // No dejamos una tarea de arranque permanente ante otro tipo de error.
                DeletePawnIoRepairTask();
            }
        }

        private static void CreatePawnIoRepairTask(string setupPath, string target)
        {
            DeletePawnIoRepairTask();
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = "Completa PawnIO después de reiniciar Windows.";
            definition.Principal.UserId = "SYSTEM";
            definition.Principal.LogonType = 5;
            definition.Principal.RunLevel = 1;
            definition.Settings.Enabled = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.ExecutionTimeLimit = "PT5M";
            definition.Settings.MultipleInstances = 2;
            dynamic trigger = definition.Triggers.Create(8);
            trigger.Enabled = true;
            dynamic action = definition.Actions.Create(0);
            action.Path = setupPath;
            action.Arguments = "--repair-pawnio \"" + target + "\"";
            action.WorkingDirectory = target;
            folder.RegisterTaskDefinition(PawnIoRepairTaskName, definition, 6, "SYSTEM", null, 5, null);
        }

        private static void DeletePawnIoRepairTask()
        {
            try
            {
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                try { folder.DeleteTask(PawnIoRepairTaskName, 0); } catch { }
            }
            catch { }
        }

        internal static bool LoadErrorReportConsent()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    if (key == null || key.GetValue("SendErrorReports") == null) return true;
                    return Convert.ToInt32(key.GetValue("SendErrorReports")) != 0;
                }
            }
            catch { return true; }
        }

        private static void SaveErrorReportConsent(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                key.SetValue("SendErrorReports", enabled ? 1 : 0, RegistryValueKind.DWord);
                if (key.GetValue("InstallationId") == null)
                    key.SetValue("InstallationId", Guid.NewGuid().ToString("D"), RegistryValueKind.String);
            }
        }

        internal static bool UseDarkTheme
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                        return value != null && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 0;
                    }
                }
                catch { return false; }
            }
        }

        internal static Color WindowBackColor
        {
            get { return UseDarkTheme ? Color.FromArgb(24, 24, 27) : Color.FromArgb(245, 246, 248); }
        }

        internal static Color WindowForeColor
        {
            get { return UseDarkTheme ? Color.White : Color.FromArgb(24, 24, 27); }
        }

        internal static Color SecondaryForeColor
        {
            get { return UseDarkTheme ? Color.DarkGray : Color.FromArgb(82, 82, 91); }
        }

        internal static Color InputBackColor
        {
            get { return UseDarkTheme ? Color.FromArgb(38, 39, 44) : Color.White; }
        }

        internal static Color ButtonBackColor
        {
            get { return UseDarkTheme ? Color.FromArgb(55, 57, 64) : Color.FromArgb(229, 231, 235); }
        }

        internal static void ApplyDarkTitleBar(IntPtr handle)
        {
            if (handle == IntPtr.Zero || Environment.OSVersion.Version.Major < 10) return;
            int enabled = UseDarkTheme ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
                int caption = ColorTranslator.ToWin32(WindowBackColor);
                int text = ColorTranslator.ToWin32(WindowForeColor);
                int border = ColorTranslator.ToWin32(UseDarkTheme
                    ? Color.FromArgb(55, 57, 64)
                    : Color.FromArgb(209, 213, 219));
                DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
                DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            }
            catch { }
        }

        private static bool IsPawnIoDriverInstalled()
        {
            string imagePath;
            int startMode;
            return TryGetPawnIoService(out imagePath, out startMode) && startMode != 4 && File.Exists(imagePath);
        }

        private static bool HasPawnIoServiceRegistration()
        {
            string imagePath;
            int startMode;
            return TryGetPawnIoService(out imagePath, out startMode);
        }

        private static bool TryRestorePawnIoDriver()
        {
            string imagePath;
            int startMode;
            if (!TryGetPawnIoService(out imagePath, out startMode) || startMode != 4 || !File.Exists(imagePath))
                return false;

            try
            {
                string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "sc.exe");
                int configExitCode;
                RunHiddenProcess(sc, "config PawnIO start= demand", out configExitCode);
                if (configExitCode != 0) return false;

                int startExitCode;
                RunHiddenProcess(sc, "start PawnIO", out startExitCode);
                if ((startExitCode == 0 || startExitCode == 1056) && IsPawnIoDriverInstalled())
                    return true;

                MarkPawnIoServiceForRepair();
                return false;
            }
            catch { return false; }
        }

        private static void MarkPawnIoServiceForRepair()
        {
            try
            {
                string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "sc.exe");
                int ignored;
                RunHiddenProcess(sc, "config PawnIO start= disabled", out ignored);
                RunHiddenProcess(sc, "delete PawnIO", out ignored);
            }
            catch { }
        }

        private static bool TryGetPawnIoService(out string imagePath, out int startMode)
        {
            imagePath = null;
            startMode = 4;
            try
            {
                using (RegistryKey service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"))
                {
                    if (service == null) return false;
                    imagePath = Convert.ToString(service.GetValue("ImagePath"));
                    if (string.IsNullOrWhiteSpace(imagePath)) return false;
                    startMode = Convert.ToInt32(service.GetValue("Start", 4), CultureInfo.InvariantCulture);

                    string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (imagePath.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                        imagePath = Path.Combine(windows, imagePath.Substring(@"\SystemRoot\".Length));
                    else if (imagePath.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
                        imagePath = imagePath.Substring(4);
                    else
                        imagePath = Environment.ExpandEnvironmentVariables(imagePath);

                    imagePath = Path.GetFullPath(imagePath);
                    return true;
                }
            }
            catch
            {
                imagePath = null;
                startMode = 4;
                return false;
            }
        }

        private static void ExtractPayload(string target)
        {
            string targetPrefix = target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("PcTemp.Payload.zip");
            if (payload == null) throw new InvalidOperationException("El instalador no contiene los archivos de PcTemp.");

            using (payload)
            using (ZipArchive archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                    if (!destination.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("El paquete contiene una ruta no válida.");

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                }
            }
        }

        private static void CreateShortcuts(string target, string setupPath, bool createDesktopShortcut)
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            string folder = Path.Combine(programs, ProductName);
            Directory.CreateDirectory(folder);
            CreateShortcut(Path.Combine(folder, "PcTemp.lnk"), Path.Combine(target, "PcTemp.exe"), "", target);
            CreateShortcut(Path.Combine(folder, "Actualizar o reparar PcTemp.lnk"), setupPath, "", target);
            CreateShortcut(Path.Combine(folder, "Desinstalar PcTemp.lnk"), setupPath, "--uninstall", target);

            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PcTemp.lnk");
            string commonDesktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "PcTemp.lnk");
            DeleteFileIfPresent(desktopShortcut);
            DeleteFileIfPresent(commonDesktopShortcut);
            if (createDesktopShortcut)
                CreateShortcut(desktopShortcut, Path.Combine(target, "PcTemp.exe"), "", target);
        }

        private static void DeleteFileIfPresent(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static void RemovePreviousInstallation(string previousTarget, string currentTarget)
        {
            if (string.IsNullOrWhiteSpace(previousTarget)) return;
            previousTarget = Path.GetFullPath(previousTarget).TrimEnd(Path.DirectorySeparatorChar);
            currentTarget = Path.GetFullPath(currentTarget).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(previousTarget, currentTarget, StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(Path.Combine(previousTarget, "PcTemp.exe"))) return;
            RemoveInstalledFiles(previousTarget);
        }

        private static void RemoveInstalledFiles(string target)
        {
            try
            {
                Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("PcTemp.Payload.zip");
                if (payload != null)
                {
                    using (payload)
                    using (ZipArchive archive = new ZipArchive(payload, ZipArchiveMode.Read))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;
                            DeleteInstalledFile(Path.Combine(target, entry.FullName));
                        }
                    }
                }
                DeleteInstalledFile(Path.Combine(target, "PcTemp-Setup.exe"));
                try { Directory.Delete(target, false); } catch { }
            }
            catch { }
        }

        private static void DeleteInstalledFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                    MoveFileEx(path, null, 4);
                else
                    File.Delete(path);
            }
            catch { }
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = Path.Combine(workingDirectory, "PcTemp.exe") + ",0";
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }

        private static void CreateStartupTask(string executablePath)
        {
            string user = WindowsIdentity.GetCurrent().Name;
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            try { folder.DeleteTask(LegacyTaskName, 0); } catch { }
            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = "Inicia PcTemp y muestra las temperaturas del equipo.";
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

        private static bool? GetRegisteredPawnIoOwnership()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(UninstallKey))
                {
                    if (key == null || key.GetValue(PawnIoManagedValue) == null) return null;
                    return Convert.ToInt32(key.GetValue(PawnIoManagedValue)) != 0;
                }
            }
            catch { return null; }
        }

        private static void WriteInstallMarker(string target, bool pawnIoManaged)
        {
            string marker = Path.Combine(target, InstallMarkerFile);
            File.WriteAllText(marker, "PcTemp installation\r\nVersion=" + Version +
                "\r\nPawnIoManaged=" + (pawnIoManaged ? "1" : "0") + "\r\n");
        }

        private static bool MarkerSaysPawnIoManaged(string target)
        {
            try
            {
                string marker = Path.Combine(target, InstallMarkerFile);
                return File.Exists(marker) && File.ReadAllText(marker)
                    .IndexOf("PawnIoManaged=1", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static void RegisterUninstaller(string target, string setupPath, bool pawnIoManaged,
            bool pawnIoRestartPending)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", ProductName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", ProductName);
                key.SetValue("InstallLocation", target);
                key.SetValue("DisplayIcon", Path.Combine(target, "PcTemp.exe") + ",0");
                key.SetValue("UninstallString", "\"" + setupPath + "\" --uninstall");
                key.SetValue("ModifyPath", "\"" + setupPath + "\"");
                key.SetValue("EstimatedSize", 12000, RegistryValueKind.DWord);
                key.SetValue(PawnIoManagedValue, pawnIoManaged ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue(PawnIoRestartPendingValue, pawnIoRestartPending ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        private static void SetPawnIoRestartPending(bool pending)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(UninstallKey))
                    key.SetValue(PawnIoRestartPendingValue, pending ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        internal static bool BeginUninstall(IWin32Window owner = null)
        {
            string target = RegisteredInstallDirectory ?? Path.GetDirectoryName(Application.ExecutablePath);
            bool removePawnIo = ShouldRemovePawnIo(target);
            DialogResult answer = ThemedMessageBox.Show(owner,
                "¿Quieres desinstalar completamente PcTemp?\n\n" +
                (removePawnIo
                    ? "También se eliminará PawnIO 2.2.0 porque fue instalado y administrado por PcTemp. "
                    : "PawnIO se conservará porque no consta como instalado por PcTemp. ") +
                "Se borrarán la carpeta del programa, los accesos directos, las tareas de inicio y toda la configuración de PcTemp.",
                "Desinstalar PcTemp", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return false;

            string temporary = Path.Combine(Path.GetTempPath(), "PcTemp-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(Application.ExecutablePath, temporary, true);
            Process.Start(new ProcessStartInfo(temporary, "--remove \"" + target + "\"") { UseShellExecute = true });
            return true;
        }

        private static bool ShouldRemovePawnIo(string target)
        {
            bool? registered = GetRegisteredPawnIoOwnership();
            return registered.HasValue ? registered.Value : MarkerSaysPawnIoManaged(target);
        }

        private static void PerformRemoval(string requestedTarget)
        {
            Thread.Sleep(800);
            string registered = RegisteredInstallDirectory;
            string expected = Path.GetFullPath(registered ?? DefaultInstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string target = Path.GetFullPath(requestedTarget).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(expected, target, StringComparison.OrdinalIgnoreCase))
            {
                ThemedMessageBox.Show(null, "La ruta de desinstalación no es válida.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                StopPcTemp();
                bool removePawnIo = ShouldRemovePawnIo(target);
                bool pawnIoRebootRequired = false;
                Exception pawnIoError = null;
                if (removePawnIo)
                {
                    try { pawnIoRebootRequired = UninstallPawnIo(target); }
                    catch (Exception ex) { pawnIoError = ex; }
                }
                DeleteStartupTasks();
                DeleteShortcuts();
                RemovePcTempRegistration();
                DeleteUserSettings();
                bool appCleanupRebootRequired = RemoveInstallationDirectory(target);
                StartSelfDeleteHelper();
                if (pawnIoError != null)
                {
                    ThemedMessageBox.Show(null, "PcTemp se ha eliminado, pero Windows no permitió retirar completamente PawnIO:\n" +
                        pawnIoError.Message + "\n\nReinicia Windows y vuelve a ejecutar el instalador si deseas repetir la limpieza.",
                        ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string message = removePawnIo
                    ? (pawnIoRebootRequired
                        ? "PcTemp se ha eliminado. Reinicia Windows para terminar de retirar PawnIO."
                        : "PcTemp y el PawnIO instalado por PcTemp se han eliminado completamente.")
                    : "PcTemp se ha eliminado completamente. PawnIO se ha conservado porque ya existía de forma independiente.";
                if (appCleanupRebootRequired)
                    message += " Reinicia Windows para terminar de borrar archivos que estaban en uso.";
                ThemedMessageBox.Show(null, message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(null, "No se pudo completar la desinstalación:\n" + ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static bool UninstallPawnIo(string pcTempDirectory)
        {
            string pawnIoDirectory = FindPawnIoInstallDirectory();
            string uninstaller = FindPawnIoUninstaller();
            if (string.IsNullOrWhiteSpace(uninstaller))
            {
                string bundled = Path.Combine(pcTempDirectory, "PawnIO_setup.exe");
                if (!File.Exists(bundled))
                {
                    if (IsPawnIoDriverInstalled())
                        throw new InvalidOperationException("No se encontró el desinstalador de PawnIO.");
                    return false;
                }
                uninstaller = bundled;
            }

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = uninstaller,
                Arguments = "-uninstall -silent",
                WorkingDirectory = Path.GetDirectoryName(uninstaller),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            };
            using (Process process = Process.Start(start))
            {
                if (process == null)
                    throw new InvalidOperationException("No se pudo iniciar el desinstalador de PawnIO.");
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    bool directoryPending = RemoveDirectoryNowOrOnReboot(pawnIoDirectory);
                    bool driverPending = false;
                    if (IsPawnIoDriverInstalled())
                    {
                        ForceRemovePawnIoDriver();
                        driverPending = true;
                    }
                    RemovePawnIoRegistration();
                    return directoryPending || driverPending;
                }
                if (process.ExitCode == 3010 || process.ExitCode == 1641)
                {
                    RemoveDirectoryNowOrOnReboot(pawnIoDirectory);
                    RemovePawnIoRegistration();
                    return true;
                }
                if (process.ExitCode == 32)
                {
                    ForceRemovePawnIoDriver();
                    if (!string.IsNullOrWhiteSpace(pawnIoDirectory))
                        ScheduleDirectoryDeletion(pawnIoDirectory);
                    RemovePawnIoRegistration();
                    return true;
                }
                throw new InvalidOperationException("PawnIO no pudo desinstalarse. Código: " + process.ExitCode + ".");
            }
        }

        private static string FindPawnIoUninstaller()
        {
            string directory = FindPawnIoInstallDirectory();
            if (!string.IsNullOrWhiteSpace(directory))
            {
                string candidate = Path.Combine(directory, "uninstall.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string FindPawnIoInstallDirectory()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                    {
                        if (key == null) continue;
                        string location = Convert.ToString(key.GetValue("InstallLocation"));
                        if (!string.IsNullOrWhiteSpace(location))
                        {
                            string fullPath = Path.GetFullPath(location);
                            if (Directory.Exists(fullPath)) return fullPath;
                        }
                    }
                }
                catch { }
            }
            string defaultDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
            if (Directory.Exists(defaultDirectory)) return defaultDirectory;
            return null;
        }

        private static void ForceRemovePawnIoDriver()
        {
            string publishedName = FindPawnIoPublishedDriverName();
            if (!string.IsNullOrWhiteSpace(publishedName))
            {
                string pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "pnputil.exe");
                int exitCode;
                RunHiddenProcess(pnputil, "/delete-driver \"" + publishedName + "\" /uninstall /force", out exitCode);
                if (exitCode != 0 && exitCode != 3010 && exitCode != 1641)
                    throw new InvalidOperationException("Windows no pudo retirar el controlador PawnIO. Código: " + exitCode + ".");
            }

            try
            {
                string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "sc.exe");
                int ignored;
                RunHiddenProcess(sc, "delete PawnIO", out ignored);
            }
            catch { }
        }

        private static string FindPawnIoPublishedDriverName()
        {
            string pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "pnputil.exe");
            int exitCode;
            string output = RunHiddenProcess(pnputil,
                "/enum-drivers /class \"{62f9c741-b25a-46ce-b54c-9bccce08b6f2}\" /format csv", out exitCode);
            if (exitCode == 0)
            {
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int marker = line.IndexOf(",\"pawnio.inf\"", StringComparison.OrdinalIgnoreCase);
                    if (marker > 0)
                        return line.Substring(0, marker).Trim().Trim('"');
                }
            }

            output = RunHiddenProcess(pnputil, "/enum-drivers", out exitCode);
            string previousValue = null;
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf(':');
                string value = separator >= 0 ? line.Substring(separator + 1).Trim() : line.Trim();
                if (line.IndexOf("pawnio.inf", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !string.IsNullOrWhiteSpace(previousValue) &&
                    previousValue.StartsWith("oem", StringComparison.OrdinalIgnoreCase) &&
                    previousValue.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
                    return previousValue;
                if (!string.IsNullOrWhiteSpace(value)) previousValue = value;
            }
            return null;
        }

        private static string RunHiddenProcess(string executable, string arguments, out int exitCode)
        {
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("No se pudo iniciar " + Path.GetFileName(executable) + ".");
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                exitCode = process.ExitCode;
                return output + Environment.NewLine + error;
            }
        }

        private static void ScheduleDirectoryDeletion(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                    MoveFileEx(file, null, 4);
                string[] directories = Directory.GetDirectories(directory, "*", SearchOption.AllDirectories);
                Array.Sort(directories, delegate(string left, string right) { return right.Length.CompareTo(left.Length); });
                foreach (string child in directories) MoveFileEx(child, null, 4);
                MoveFileEx(directory, null, 4);
            }
            catch { }
        }

        private static bool RemoveDirectoryNowOrOnReboot(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
            try
            {
                Directory.Delete(directory, true);
                return false;
            }
            catch
            {
                ScheduleDirectoryDeletion(directory);
                return true;
            }
        }

        private static bool RemoveInstallationDirectory(string target)
        {
            target = ValidateInstallDirectory(target);
            bool marker = File.Exists(Path.Combine(target, InstallMarkerFile));
            bool applicationFiles = File.Exists(Path.Combine(target, "PcTemp.exe")) &&
                File.Exists(Path.Combine(target, "PcTemp-Setup.exe"));
            if (!marker && !applicationFiles)
                throw new InvalidOperationException("La carpeta no contiene una instalación válida de PcTemp.");
            return RemoveDirectoryNowOrOnReboot(target);
        }

        private static void StartSelfDeleteHelper()
        {
            try
            {
                string executable = Path.GetFullPath(Application.ExecutablePath);
                string script = Path.Combine(Path.GetTempPath(), "PcTemp-Cleanup-" + Guid.NewGuid().ToString("N") + ".cmd");
                File.WriteAllText(script, "@echo off\r\n:retry\r\ndel /f /q \"" + executable +
                    "\" >nul 2>&1\r\nif exist \"" + executable +
                    "\" (\r\n  ping 127.0.0.1 -n 2 >nul\r\n  goto retry\r\n)\r\ndel /f /q \"%~f0\"\r\n");
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = "/d /c \"\"" + script + "\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { MoveFileEx(Application.ExecutablePath, null, 4); }
        }

        private static void DeleteShortcuts()
        {
            foreach (string programs in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            })
            {
                try
                {
                    string folder = Path.Combine(programs, ProductName);
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
                catch { }
            }
            DeleteFileIfPresent(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PcTemp.lnk"));
            DeleteFileIfPresent(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "PcTemp.lnk"));
        }

        private static void RemovePcTempRegistration()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        machine.DeleteSubKeyTree(UninstallKey, false);
                }
                catch { }
            }
        }

        private static void RemovePawnIoRegistration()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        machine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO", false);
                }
                catch { }
            }
        }

        private static void DeleteUserSettings()
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (run != null)
                    {
                        run.DeleteValue("PcTemp", false);
                        run.DeleteValue("TempBandeja", false);
                    }
                }
            }
            catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\PcTemp", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\TempBandeja", false); } catch { }
        }

        private static void StopPcTemp()
        {
            foreach (Process process in Process.GetProcessesByName("PcTemp"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }

        private static void DeleteStartupTasks()
        {
            try
            {
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                try { folder.DeleteTask(TaskName, 0); } catch { }
                try { folder.DeleteTask(LegacyTaskName, 0); } catch { }
                try { folder.DeleteTask(PawnIoRepairTaskName, 0); } catch { }
            }
            catch { }
        }
    }

    internal static class ThemedMessageBox
    {
        internal static DialogResult Show(IWin32Window owner, string message, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using (ThemedMessageDialog dialog = new ThemedMessageDialog(message, caption, buttons, icon))
                return owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        }

        private sealed class ThemedMessageDialog : Form
        {
            internal ThemedMessageDialog(string message, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
            {
                Text = caption;
                ClientSize = new Size(500, 220);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                BackColor = Program.WindowBackColor;
                ForeColor = Program.WindowForeColor;
                Font = new Font("Segoe UI", 9F);

                PictureBox image = new PictureBox
                {
                    Image = GetDialogIcon(icon).ToBitmap(),
                    Location = new Point(24, 28),
                    Size = new Size(40, 40),
                    SizeMode = PictureBoxSizeMode.StretchImage
                };
                Controls.Add(image);

                Label text = new Label
                {
                    Text = message,
                    Location = new Point(82, 24),
                    Size = new Size(390, 130),
                    ForeColor = Program.WindowForeColor,
                    BackColor = Program.WindowBackColor
                };
                Controls.Add(text);

                FlowLayoutPanel actions = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 58,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(12, 11, 12, 10),
                    BackColor = Program.UseDarkTheme
                        ? Color.FromArgb(31, 31, 35)
                        : Color.FromArgb(235, 237, 240)
                };
                Controls.Add(actions);

                if (buttons == MessageBoxButtons.YesNo)
                {
                    Button yes = CreateButton("Sí", DialogResult.Yes);
                    Button no = CreateButton("No", DialogResult.No);
                    actions.Controls.Add(yes);
                    actions.Controls.Add(no);
                    AcceptButton = yes;
                    CancelButton = no;
                }
                else
                {
                    Button ok = CreateButton("Aceptar", DialogResult.OK);
                    actions.Controls.Add(ok);
                    AcceptButton = ok;
                    CancelButton = ok;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Program.ApplyDarkTitleBar(Handle);
            }

            private static Button CreateButton(string text, DialogResult result)
            {
                return new Button
                {
                    Text = text,
                    DialogResult = result,
                    Size = new Size(105, 34),
                    Margin = new Padding(8, 0, 0, 0),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Program.ButtonBackColor,
                    ForeColor = Program.WindowForeColor,
                    UseVisualStyleBackColor = false,
                    Cursor = Cursors.Hand
                };
            }

            private static Icon GetDialogIcon(MessageBoxIcon icon)
            {
                if (icon == MessageBoxIcon.Error) return SystemIcons.Error;
                if (icon == MessageBoxIcon.Warning) return SystemIcons.Warning;
                if (icon == MessageBoxIcon.Question) return SystemIcons.Question;
                return SystemIcons.Information;
            }
        }
    }

    internal sealed class LargeCheckBox : CheckBox
    {
        private bool _hovered;

        internal LargeCheckBox()
        {
            AutoSize = false;
            Size = new Size(506, 30);
            Font = new Font("Segoe UI", 10F);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            const int boxSize = 24;
            Rectangle box = new Rectangle(1, (Height - boxSize) / 2, boxSize, boxSize);
            Color accent = Color.FromArgb(0, 174, 239);
            Color border = _hovered
                ? accent
                : (Program.UseDarkTheme ? Color.FromArgb(96, 99, 108) : Color.FromArgb(128, 134, 145));
            Color fill = Checked ? accent : Program.InputBackColor;

            using (SolidBrush background = new SolidBrush(fill))
            using (Pen outline = new Pen(border, 1.5F))
            {
                e.Graphics.FillRectangle(background, box);
                e.Graphics.DrawRectangle(outline, box);
            }

            if (Checked)
            {
                using (Pen check = new Pen(Color.White, 2.7F))
                {
                    check.StartCap = LineCap.Round;
                    check.EndCap = LineCap.Round;
                    check.LineJoin = LineJoin.Round;
                    e.Graphics.DrawLines(check, new[]
                    {
                        new Point(box.Left + 5, box.Top + 12),
                        new Point(box.Left + 10, box.Bottom - 6),
                        new Point(box.Right - 4, box.Top + 6)
                    });
                }
            }

            TextRenderer.DrawText(e.Graphics, Text, Font,
                new Rectangle(box.Right + 16, 0, Math.Max(1, Width - box.Right - 16), Height),
                Enabled ? ForeColor : Program.SecondaryForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            Invalidate();
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly Label _status;
        private readonly Button _install;
        private readonly Button _uninstall;
        private readonly Label _installPathLabel;
        private readonly TextBox _installPath;
        private readonly Button _browse;
        private readonly CheckBox _desktopShortcut;
        private readonly CheckBox _errorReports;
        private readonly Label _errorReportsInfo;
        private readonly Label _features;

        public SetupForm()
        {
            bool installed = Program.IsInstalled;
            Text = "Instalador de PcTemp " + Program.Version;
            ClientSize = new Size(570, 510);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Program.WindowBackColor;
            ForeColor = Program.WindowForeColor;
            Font = new Font("Segoe UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Controls.Add(new Label
            {
                Text = "PcTemp " + Program.Version,
                Font = new Font("Segoe UI Semibold", 24F),
                ForeColor = Color.DeepSkyBlue,
                AutoSize = true,
                Location = new Point(28, 22)
            });
            Controls.Add(new Label
            {
                Text = "Temperaturas de CPU, GPU, placa base y discos.",
                Font = new Font("Segoe UI", 11F),
                AutoSize = true,
                Location = new Point(31, 75)
            });
            _installPathLabel = new Label
            {
                Text = "Carpeta de instalación:",
                ForeColor = Program.WindowForeColor,
                AutoSize = true,
                Location = new Point(32, 184)
            };
            Controls.Add(_installPathLabel);

            _installPath = new TextBox
            {
                Text = Program.SuggestedInstallDirectory,
                Location = new Point(32, 208),
                Size = new Size(390, 25),
                BackColor = Program.InputBackColor,
                ForeColor = Program.WindowForeColor,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_installPath);

            _browse = new Button
            {
                Text = "Examinar…",
                Location = new Point(434, 206),
                Size = new Size(104, 29),
                BackColor = Program.ButtonBackColor,
                ForeColor = Program.WindowForeColor,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            _browse.Click += BrowseClicked;
            Controls.Add(_browse);

            _desktopShortcut = new LargeCheckBox
            {
                Text = "Crear acceso directo en el escritorio",
                Checked = installed ? Program.DesktopShortcutExists : true,
                Location = new Point(32, 252),
                BackColor = Program.WindowBackColor,
                ForeColor = Program.WindowForeColor,
                UseVisualStyleBackColor = false
            };
            Controls.Add(_desktopShortcut);

            _errorReports = new LargeCheckBox
            {
                Text = "Ayudar a mejorar la aplicación enviando informes anónimos de fallos",
                Checked = Program.LoadErrorReportConsent(),
                Location = new Point(32, 302),
                BackColor = Program.WindowBackColor,
                ForeColor = Program.WindowForeColor,
                UseVisualStyleBackColor = false,
                Visible = !installed
            };
            Controls.Add(_errorReports);

            _errorReportsInfo = new Label
            {
                Text = "Privacidad: la opción viene marcada, puedes desmarcarla antes de instalar o cambiarla después desde la aplicación.",
                ForeColor = Program.SecondaryForeColor,
                AutoSize = false,
                Size = new Size(466, 42),
                Location = new Point(70, 337),
                Visible = !installed
            };
            Controls.Add(_errorReportsInfo);

            _features = new Label
            {
                Text = "✓ PawnIO 2.2.0 integrado\n✓ Inicio automático con Windows\n✓ Desinstalación completa de archivos, tareas y registro",
                ForeColor = Program.WindowForeColor,
                AutoSize = true,
                Location = new Point(32, 112)
            };
            Controls.Add(_features);

            _status = new Label
            {
                Text = installed ? "PcTemp ya está instalado. Puedes actualizarlo o repararlo." : "Listo para instalar.",
                ForeColor = Program.SecondaryForeColor,
                AutoSize = false,
                Size = new Size(506, 42),
                Location = new Point(32, 400)
            };
            Controls.Add(_status);

            _install = new Button
            {
                Text = installed ? "Actualizar o reparar" : "Instalar PcTemp",
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(170, 36),
                Location = new Point(368, 458),
                Cursor = Cursors.Hand
            };
            _install.Click += InstallClicked;
            Controls.Add(_install);

            _uninstall = new Button
            {
                Text = "Desinstalar PcTemp",
                BackColor = Color.FromArgb(150, 48, 48),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(170, 36),
                Location = new Point(178, 458),
                Cursor = Cursors.Hand,
                Visible = installed
            };
            _uninstall.Click += delegate
            {
                if (Program.BeginUninstall(this)) Close();
            };
            Controls.Add(_uninstall);
            if (installed) ApplyCompletedLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Program.ApplyDarkTitleBar(Handle);
        }

        private void ApplyCompletedLayout()
        {
            _installPathLabel.Visible = false;
            _installPath.Visible = false;
            _browse.Visible = false;
            _desktopShortcut.Visible = false;
            _features.Visible = false;
            _errorReports.Visible = false;
            _errorReportsInfo.Visible = false;
            _status.Location = new Point(32, 125);
            _install.Location = new Point(368, 185);
            _uninstall.Location = new Point(178, 185);
            _uninstall.Visible = true;
            ClientSize = new Size(570, 245);
        }

        private void ApplyFinishedLayout()
        {
            _installPathLabel.Visible = false;
            _installPath.Visible = false;
            _browse.Visible = false;
            _desktopShortcut.Visible = false;
            _features.Visible = false;
            _errorReports.Visible = false;
            _errorReportsInfo.Visible = false;
            _uninstall.Visible = false;
            _status.Location = new Point(32, 125);
            _install.Location = new Point(368, 185);
            _install.Text = "Cerrar instalador";
            _install.Click -= InstallClicked;
            _install.Click += delegate { Close(); };
            _install.Enabled = true;
            ClientSize = new Size(570, 245);
        }

        private void BrowseClicked(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Selecciona la carpeta donde se instalará PcTemp.";
                dialog.ShowNewFolderButton = true;
                try
                {
                    string selected = Program.ValidateInstallDirectory(_installPath.Text);
                    if (Directory.Exists(selected)) dialog.SelectedPath = selected;
                }
                catch { }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _installPath.Text = dialog.SelectedPath;
            }
        }

        private void InstallClicked(object sender, EventArgs e)
        {
            _install.Enabled = false;
            _status.Text = "Instalando PcTemp…";
            Cursor = Cursors.WaitCursor;
            Refresh();
            try
            {
                string target = Program.ValidateInstallDirectory(_installPath.Text);
                _installPath.Text = target;
                bool rebootRequired = Program.Install(target, _desktopShortcut.Checked, _errorReports.Checked, delegate(string message)
                {
                    _status.Text = message;
                    _status.Refresh();
                });
                _status.Text = rebootRequired
                    ? "Instalación completa. Reinicia Windows para activar los sensores."
                    : "PcTemp y PawnIO se han instalado correctamente.";
                _status.ForeColor = Program.UseDarkTheme ? Color.LightGreen : Color.ForestGreen;
                ApplyFinishedLayout();
            }
            catch (Exception ex)
            {
                _status.Text = "Error: " + ex.Message;
                _status.ForeColor = Color.OrangeRed;
                _install.Enabled = true;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }
    }
}
