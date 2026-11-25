using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public sealed class GitHubPackagePublisherWindow : EditorWindow
{
    private string _ghPath;
    private string _accountName;
    private string _sourcePath = string.Empty;
    private string _packagePath = string.Empty;
    private string _repositoryName = string.Empty;
    private string _description = string.Empty;
    private string _releaseTag = "v1.0.0";
    private string _releaseTitle = string.Empty;
    private string _releaseNotes = string.Empty;
    private bool _makePublic = true;
    private bool _createReadmeIfMissing = true;
    private bool _createGitignoreIfMissing = true;
    private bool _addMitLicense = true;
    private bool _createWorkingCopy = true;
    private string _copyrightHolder = string.Empty;
    private string _workingCopyParentPath = string.Empty;
    private bool _loginTerminalOpened;
    private Vector2 _scrollPosition;
    private Process _loginProcess;
    private double _nextLoginCheckTime;
    private double _repositoryCheckTime;
    private bool _repositoryCheckPending;
    private bool? _repositoryNameAvailable;
    private string _repositoryCheckName = string.Empty;
    private string _repositoryCheckMessage = string.Empty;
    private MessageType _repositoryCheckType = MessageType.Info;
    private string _statusMessage = "Connect your GitHub account to continue.";
    private MessageType _statusType = MessageType.Info;
    private bool _publishSucceeded;

    [MenuItem("Tools/GitHub Publish Custom Package")]
    private static void Open()
    {
        GetWindow<GitHubPackagePublisherWindow>("GitHub Package Publisher");
    }

    private void OnEnable()
    {
        _ghPath = FindGhPath();
        _workingCopyParentPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "UnityTools");
        if (!string.IsNullOrEmpty(_ghPath))
            CheckGitHubLogin(false);

        EditorApplication.update += PollGitHubLogin;
    }

    private void OnDisable()
    {
        EditorApplication.update -= PollGitHubLogin;
        StopLoginProcess();
    }

    private void PollGitHubLogin()
    {
        PollRepositoryNameCheck();

        if (string.IsNullOrEmpty(_ghPath) ||
            !string.IsNullOrEmpty(_accountName) ||
            !_loginTerminalOpened ||
            EditorApplication.timeSinceStartup < _nextLoginCheckTime)
        {
            return;
        }

        if (_loginProcess != null && _loginProcess.HasExited)
        {
            StopLoginProcess();
            CheckGitHubLogin(false);
            Repaint();
            return;
        }

        _nextLoginCheckTime = EditorApplication.timeSinceStartup + 3d;
        var result = RunGh("api user --jq .login", 10000);
        if (result.Succeeded)
        {
            _accountName = result.Output.Trim();
            if (string.IsNullOrWhiteSpace(_copyrightHolder))
                _copyrightHolder = _accountName;
            StopLoginProcess();
            _statusType = MessageType.Info;
            _statusMessage = "Connected as " + _accountName + ". Choose a package and repository settings below.";
            Repaint();
        }
    }

    internal sealed class GitHubPublishSuccessWindow : EditorWindow
    {
        private const float WindowWidth = 520f;
        private const float WindowHeight = 250f;
        private string _repositoryUrl;
        private string _releaseUrl;
        private string _workingCopyPath;

        internal static void Show(string repositoryUrl, string releaseUrl, string workingCopyPath)
        {
            var window = CreateInstance<GitHubPublishSuccessWindow>();
            window._repositoryUrl = repositoryUrl;
            window._releaseUrl = releaseUrl;
            window._workingCopyPath = workingCopyPath;
            window.titleContent = new GUIContent("Publish complete");
            window.minSize = new Vector2(WindowWidth, WindowHeight);
            window.maxSize = new Vector2(WindowWidth, WindowHeight);
            window.ShowModalUtility();
        }

        private void OnGUI()
        {
            var background = EditorGUIUtility.isProSkin
                ? new Color(0.12f, 0.28f, 0.19f)
                : new Color(0.82f, 0.96f, 0.86f);
            var foreground = EditorGUIUtility.isProSkin
                ? new Color(0.72f, 1f, 0.78f)
                : new Color(0.08f, 0.35f, 0.16f);
            EditorGUI.DrawRect(new Rect(0f, 0f, position.width, position.height), background);

            GUILayout.Space(20f);
            var oldColor = GUI.contentColor;
            GUI.contentColor = foreground;
            EditorGUILayout.LabelField("PUBLISH SUCCESS", EditorStyles.boldLabel);
            GUI.contentColor = oldColor;
            GUILayout.Space(8f);
            EditorGUILayout.LabelField("Source code and Unity package published.", EditorStyles.wordWrappedLabel);
            GUILayout.Space(8f);
            DrawLink("Repository", _repositoryUrl);
            DrawLink("Release", _releaseUrl);
            if (!string.IsNullOrEmpty(_workingCopyPath))
                EditorGUILayout.LabelField("Local Git copy", _workingCopyPath, EditorStyles.wordWrappedLabel);

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("OK", GUILayout.Height(30f)))
                Close();
            GUILayout.Space(12f);
        }

        private static void DrawLink(string label, string url)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(90f));
            if (GUILayout.Button(url, EditorStyles.linkLabel))
                Application.OpenURL(url);
            EditorGUILayout.EndHorizontal();
        }
    }

    private void OnGUI()
    {
        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
        DrawWindowContent();
        EditorGUILayout.EndScrollView();
    }

    private void DrawWindowContent()
    {
        EditorGUILayout.LabelField("GitHub Publish Custom Package", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Publish source code and a Unity package to GitHub.",
            MessageType.Info);

        if (string.IsNullOrEmpty(_ghPath))
        {
            if (GUILayout.Button("Install GitHub connection helper (one time)"))
                InstallGitHubCli();

            if (GUILayout.Button("Check GitHub CLI installation"))
            {
                _ghPath = FindGhPath();
                _statusType = string.IsNullOrEmpty(_ghPath) ? MessageType.Warning : MessageType.Info;
                _statusMessage = string.IsNullOrEmpty(_ghPath)
                    ? "GitHub CLI was not detected. Restart Unity after installing it, then check again."
                    : "GitHub CLI detected. Connect your GitHub account to continue.";
            }

            EditorGUILayout.HelpBox(_statusMessage, _statusType);
            return;
        }

        if (string.IsNullOrEmpty(_accountName))
        {
            if (_loginTerminalOpened)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Waiting for GitHub connection...");
                if (GUILayout.Button("Cancel", GUILayout.Width(84f)))
                    CancelLogin();
                EditorGUILayout.EndHorizontal();
            }
            else if (GUILayout.Button("Connect GitHub", GUILayout.Height(34f)))
                CheckGitHubLogin();

            if (!string.IsNullOrEmpty(_statusMessage) && _statusType != MessageType.Info)
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            return;
        }

        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        var previousContentColor = GUI.contentColor;
        GUI.contentColor = new Color(0.25f, 0.8f, 0.35f);
        EditorGUILayout.LabelField("CONNECTED", EditorStyles.boldLabel, GUILayout.Width(88f));
        GUI.contentColor = previousContentColor;
        EditorGUILayout.LabelField(_accountName);
        if (GUILayout.Button("Log out", GUILayout.Width(84f)))
            LogOut();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Package to upload", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        var packageMissing = string.IsNullOrEmpty(_packagePath) || !File.Exists(_packagePath);
        DrawPathField(
            "Package",
            packageMissing ? string.Empty : _packagePath,
            "Choose a .unitypackage...");
        if (GUILayout.Button("Browse...", GUILayout.Width(84f)))
        {
            var selectedPath = EditorUtility.OpenFilePanel(
                "Select Unity package to publish",
                string.IsNullOrEmpty(_packagePath)
                    ? Directory.GetParent(Application.dataPath).FullName
                    : Path.GetDirectoryName(_packagePath),
                "unitypackage");
            if (!string.IsNullOrEmpty(selectedPath))
                _packagePath = selectedPath;
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Source code", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        var sourceMissing = string.IsNullOrEmpty(_sourcePath) || !Directory.Exists(_sourcePath);
        DrawPathField(
            "Folder",
            sourceMissing ? string.Empty : _sourcePath,
            "Choose source folder...");
        if (GUILayout.Button("Browse...", GUILayout.Width(84f)))
        {
            var selectedPath = EditorUtility.OpenFolderPanel(
                "Select source code folder",
                string.IsNullOrEmpty(_sourcePath)
                    ? Directory.GetParent(Application.dataPath).FullName
                    : _sourcePath,
                string.Empty);
            if (!string.IsNullOrEmpty(selectedPath))
            {
                _sourcePath = selectedPath;
                _repositoryName = CreateRepositoryNameFromSource(selectedPath);
                ScheduleRepositoryNameCheck();
            }
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Repository", EditorStyles.boldLabel);
        var repositoryNeedsAttention = !IsRepositoryNameValid(_repositoryName) ||
                                       _repositoryNameAvailable == false;
        EditorGUI.BeginChangeCheck();
        _repositoryName = TextFieldWithHint(
            "Repository name",
            _repositoryName,
            repositoryNeedsAttention && string.IsNullOrWhiteSpace(_repositoryName)
                ? "Enter repository name..."
                : repositoryNeedsAttention ? "Name unavailable or invalid" : string.Empty);
        if (EditorGUI.EndChangeCheck())
            ScheduleRepositoryNameCheck();
        if (!string.IsNullOrEmpty(_repositoryCheckMessage))
            EditorGUILayout.HelpBox(_repositoryCheckMessage, _repositoryCheckType);
        _description = EditorGUILayout.TextField("Description", _description);
        _makePublic = EditorGUILayout.Toggle("Public repository", _makePublic);

        var sourceReadme = string.IsNullOrEmpty(_sourcePath) ? null : FindReadme(_sourcePath);
        if (sourceReadme == null)
        {
            _createReadmeIfMissing = EditorGUILayout.Toggle("Create README.md", _createReadmeIfMissing);
        }
        else
            DrawFoundFileStatus("README.md found in source");

        var sourceGitignore = string.IsNullOrEmpty(_sourcePath)
            ? null
            : Path.Combine(_sourcePath, ".gitignore");
        if (string.IsNullOrEmpty(sourceGitignore) || !File.Exists(sourceGitignore))
            _createGitignoreIfMissing = EditorGUILayout.Toggle("Create Unity .gitignore", _createGitignoreIfMissing);
        else
            DrawFoundFileStatus(".gitignore found in source");

        var sourceLicense = string.IsNullOrEmpty(_sourcePath) ? null : FindLicense(_sourcePath);
        if (sourceLicense == null)
        {
            _addMitLicense = EditorGUILayout.Toggle("Add MIT License", _addMitLicense);
            if (_addMitLicense)
                _copyrightHolder = TextFieldWithHint(
                    "Copyright holder",
                    _copyrightHolder,
                    string.IsNullOrWhiteSpace(_copyrightHolder) ? "Enter copyright holder..." : string.Empty);
        }
        else
        {
            DrawFoundFileStatus(Path.GetFileName(sourceLicense) + " found in source");
        }

        _createWorkingCopy = EditorGUILayout.Toggle("Create separate Git working copy", _createWorkingCopy);
        if (_createWorkingCopy)
        {
            EditorGUILayout.BeginHorizontal();
            DrawPathField(
                "Local folder",
                string.IsNullOrEmpty(_workingCopyParentPath) ? string.Empty : GetWorkingCopyPath(),
                "Choose a folder outside the Unity project...");
            if (GUILayout.Button("Browse...", GUILayout.Width(84f)))
            {
                var selectedPath = EditorUtility.OpenFolderPanel(
                    "Choose parent folder for the Git working copy",
                    Directory.Exists(_workingCopyParentPath)
                        ? _workingCopyParentPath
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    string.Empty);
                if (!string.IsNullOrEmpty(selectedPath))
                    _workingCopyParentPath = selectedPath;
            }

            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_repositoryName) && !string.IsNullOrEmpty(_workingCopyParentPath))
            {
                var workingCopyPath = GetWorkingCopyPath();
                if (IsInsideUnityProject(workingCopyPath))
                    EditorGUILayout.HelpBox("Choose a folder outside the current Unity project.", MessageType.Error);
                else if (Directory.Exists(workingCopyPath) || File.Exists(workingCopyPath))
                    EditorGUILayout.HelpBox("The local working-copy destination already exists.", MessageType.Error);
            }
        }

        EditorGUILayout.LabelField("GitHub release", EditorStyles.boldLabel);
        _releaseTag = TextFieldWithHint(
            "Tag",
            _releaseTag,
            string.IsNullOrWhiteSpace(_releaseTag) ? "Enter release tag..." : string.Empty);
        _releaseTitle = EditorGUILayout.TextField("Title", _releaseTitle);
        EditorGUILayout.LabelField("Release notes");
        _releaseNotes = EditorGUILayout.TextArea(_releaseNotes, GUILayout.MinHeight(56f));

        var canPublish = IsRepositoryNameValid(_repositoryName) &&
                         _repositoryNameAvailable == true &&
                         !_repositoryCheckPending &&
                         !string.IsNullOrEmpty(_sourcePath) &&
                         Directory.Exists(_sourcePath) &&
                         !string.IsNullOrEmpty(_packagePath) &&
                         File.Exists(_packagePath) &&
                         (sourceLicense != null || !_addMitLicense || !string.IsNullOrWhiteSpace(_copyrightHolder)) &&
                         (!_createWorkingCopy || IsWorkingCopyDestinationAvailable()) &&
                         !string.IsNullOrWhiteSpace(_releaseTag);
        using (new EditorGUI.DisabledScope(!canPublish))
        {
            if (GUILayout.Button("Create repository and publish release", GUILayout.Height(36f)))
                CreateRepositoryAndUploadPackage();
        }

        if (_publishSucceeded)
            DrawPublishSuccess();
        else
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
    }

    private void DrawPublishSuccess()
    {
        var rect = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        var previousContentColor = GUI.contentColor;
        GUI.contentColor = new Color(0.25f, 0.8f, 0.35f);
        EditorGUILayout.LabelField("PUBLISH SUCCESS", EditorStyles.boldLabel);
        GUI.contentColor = previousContentColor;
        EditorGUILayout.LabelField(_statusMessage, EditorStyles.wordWrappedLabel);
        EditorGUILayout.EndVertical();
    }

    private static void DrawPathField(string label, string value, string hint)
    {
        var rect = EditorGUILayout.GetControlRect();
        rect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
        if (string.IsNullOrEmpty(value))
        {
            var style = new GUIStyle(EditorStyles.textField);
            style.normal.textColor = Color.red;
            style.hover.textColor = Color.red;
            style.focused.textColor = Color.red;
            style.active.textColor = Color.red;
            GUI.Label(rect, hint, style);
        }
        else
        {
            GUI.Label(rect, value, EditorStyles.textField);
        }
    }

    private static void DrawFoundFileStatus(string message)
    {
        var previousColor = GUI.contentColor;
        GUI.contentColor = new Color(0.25f, 0.8f, 0.35f);
        EditorGUILayout.LabelField(message, EditorStyles.label);
        GUI.contentColor = previousColor;
    }

    private static string TextFieldWithHint(string label, string value, string hint)
    {
        var rect = EditorGUILayout.GetControlRect();
        rect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
        if (!string.IsNullOrEmpty(value) || string.IsNullOrEmpty(hint))
            return EditorGUI.TextField(rect, value);

        var style = new GUIStyle(EditorStyles.textField);
        style.normal.textColor = Color.red;
        style.hover.textColor = Color.red;
        style.focused.textColor = Color.red;
        style.active.textColor = Color.red;
        var result = EditorGUI.TextField(rect, hint, style);
        return result == hint ? string.Empty : result;
    }

    private string GetWorkingCopyPath()
    {
        return Path.Combine(_workingCopyParentPath, _repositoryName);
    }

    private bool IsWorkingCopyDestinationAvailable()
    {
        if (string.IsNullOrWhiteSpace(_workingCopyParentPath) ||
            !IsRepositoryNameValid(_repositoryName))
        {
            return false;
        }

        try
        {
            var destination = GetWorkingCopyPath();
            return !IsInsideUnityProject(destination) &&
                   !Directory.Exists(destination) &&
                   !File.Exists(destination);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsInsideUnityProject(string path)
    {
        var projectRoot = Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(projectRoot, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(projectRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void CheckGitHubLogin(bool startLoginIfNeeded = true)
    {
        var authResult = RunGh("auth status --hostname github.com", 30000);
        if (!authResult.Succeeded)
        {
            _accountName = null;
            var needsLogin =
                authResult.Output.IndexOf("not logged", StringComparison.OrdinalIgnoreCase) >= 0 ||
                authResult.Output.IndexOf("To log in", StringComparison.OrdinalIgnoreCase) >= 0;
            if (needsLogin)
            {
                _statusType = MessageType.Warning;
                _statusMessage = startLoginIfNeeded ? "Sign in in the opened window." : "Not connected.";
                if (startLoginIfNeeded && !_loginTerminalOpened)
                    OpenLoginTerminal();
                Repaint();
            }
            else
            {
                _statusType = MessageType.Error;
                _statusMessage = "Could not verify the GitHub connection.\n" + authResult.Output;
            }

            return;
        }

        var result = RunGh("api user --jq .login", 30000);
        if (!result.Succeeded)
        {
            _accountName = null;
            _statusType = MessageType.Error;
            _statusMessage = "GitHub authentication is present, but the account could not be verified.\n" + result.Output;
            return;
        }

        _accountName = result.Output.Trim();
        if (string.IsNullOrWhiteSpace(_copyrightHolder))
            _copyrightHolder = _accountName;
        _loginTerminalOpened = false;
        ScheduleRepositoryNameCheck();
        _statusType = MessageType.Info;
        _statusMessage = "Connected to GitHub.";
    }

    private void ScheduleRepositoryNameCheck()
    {
        _repositoryNameAvailable = null;
        _repositoryCheckName = _repositoryName;
        _repositoryCheckPending = !string.IsNullOrEmpty(_accountName) &&
                                  IsRepositoryNameValid(_repositoryName);
        if (_repositoryCheckPending)
        {
            _repositoryCheckMessage = "Checking repository name...";
            _repositoryCheckType = MessageType.Info;
        }
        else if (!string.IsNullOrEmpty(_accountName) && !string.IsNullOrWhiteSpace(_repositoryName))
        {
            _repositoryCheckMessage = "Use 1-100 letters, numbers, dots, hyphens, or underscores.";
            _repositoryCheckType = MessageType.Warning;
        }
        else
        {
            _repositoryCheckMessage = string.Empty;
            _repositoryCheckType = MessageType.Info;
        }

        _repositoryCheckTime = EditorApplication.timeSinceStartup + 0.6d;
        Repaint();
    }

    private void PollRepositoryNameCheck()
    {
        if (!_repositoryCheckPending || EditorApplication.timeSinceStartup < _repositoryCheckTime)
            return;

        if (string.IsNullOrEmpty(_accountName) || _repositoryCheckName != _repositoryName ||
            !IsRepositoryNameValid(_repositoryName))
        {
            _repositoryCheckPending = false;
            return;
        }

        _repositoryCheckPending = false;
        var repository = _accountName + "/" + _repositoryName;
        var result = RunGh("api repos/" + repository + " --jq .full_name", 10000);
        if (result.Succeeded)
        {
            _repositoryNameAvailable = false;
            _repositoryCheckMessage = "Already exists in " + _accountName + "'s GitHub account.";
            _repositoryCheckType = MessageType.Error;
        }
        else if (result.Output.IndexOf("HTTP 404", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 result.Output.IndexOf("Not Found", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _repositoryNameAvailable = true;
            _repositoryCheckMessage = "Repository name is available.";
            _repositoryCheckType = MessageType.Info;
        }
        else
        {
            _repositoryNameAvailable = null;
            _repositoryCheckMessage = "Could not check repository name.\n" + result.Output;
            _repositoryCheckType = MessageType.Warning;
        }

        Repaint();
    }

    private static string CreateRepositoryNameFromSource(string sourcePath)
    {
        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        name = Regex.Replace(name, @"[^A-Za-z0-9._-]+", "-").Trim('.', '-', '_');
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - 4).Trim('.', '-', '_');
        return name;
    }

    private static string FindReadme(string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            return null;

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase))
                return file;
        }

        return null;
    }

    private static string FindLicense(string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            return null;

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    private static string CreateMitLicense(string copyrightHolder)
    {
        return "MIT License\n\n" +
               "Copyright (c) " + DateTime.Now.Year + " " + copyrightHolder.Trim() + "\n\n" +
               "Permission is hereby granted, free of charge, to any person obtaining a copy\n" +
               "of this software and associated documentation files (the \"Software\"), to deal\n" +
               "in the Software without restriction, including without limitation the rights\n" +
               "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n" +
               "copies of the Software, and to permit persons to whom the Software is\n" +
               "furnished to do so, subject to the following conditions:\n\n" +
               "The above copyright notice and this permission notice shall be included in all\n" +
               "copies or substantial portions of the Software.\n\n" +
               "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR\n" +
               "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,\n" +
               "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE\n" +
               "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\n" +
               "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,\n" +
               "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE\n" +
               "SOFTWARE.\n";
    }

    private void OpenLoginTerminal()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ghPath,
                Arguments = "auth login --hostname github.com --web --git-protocol https",
                UseShellExecute = true
            };
            _loginProcess = Process.Start(startInfo);
            if (_loginProcess == null)
                throw new InvalidOperationException("GitHub CLI did not start.");

            _loginTerminalOpened = true;
            _nextLoginCheckTime = EditorApplication.timeSinceStartup + 3d;
            _statusType = MessageType.Info;
            _statusMessage = "Waiting for sign-in...";
        }
        catch (Exception exception)
        {
            _loginTerminalOpened = false;
            _statusType = MessageType.Error;
            _statusMessage = "Could not open sign-in: " + exception.Message;
        }
    }

    private void CancelLogin()
    {
        StopLoginProcess();
        _loginTerminalOpened = false;
        _statusType = MessageType.Error;
        _statusMessage = "Sign-in cancelled. Not connected.";
        Repaint();
    }

    private void StopLoginProcess()
    {
        _loginTerminalOpened = false;
        if (_loginProcess == null)
            return;

        try
        {
            if (!_loginProcess.HasExited)
                _loginProcess.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            UnityEngine.Debug.LogWarning("Could not stop GitHub CLI sign-in: " + exception.Message);
        }
        finally
        {
            _loginProcess.Dispose();
            _loginProcess = null;
        }
    }

    private void InstallGitHubCli()
    {
        if (!EditorUtility.DisplayDialog(
                "Install GitHub connection helper",
                "Unity will open a terminal and ask Windows Package Manager (winget) to install GitHub CLI. This is a one-time local install. After it finishes, restart Unity and reopen this tool.",
                "Install",
                "Cancel"))
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/K winget install --id GitHub.cli --exact",
                UseShellExecute = true
            };
            Process.Start(startInfo);
            _statusType = MessageType.Info;
            _statusMessage =
                "The installer terminal was opened. When installation completes, restart Unity and reopen this window.";
        }
        catch (Exception exception)
        {
            _statusType = MessageType.Error;
            _statusMessage =
                "Could not start winget. Install GitHub CLI using your usual Windows software installer, then restart Unity.\n" +
                exception.Message;
        }
    }

    private void LogOut()
    {
        if (!EditorUtility.DisplayDialog(
                "Log out of GitHub",
                "Sign out " + _accountName + " from GitHub CLI on this computer?",
                "Log out",
                "Cancel"))
        {
            return;
        }

        var result = RunGh(
            "auth logout --hostname github.com --user " + QuoteArgument(_accountName),
            30000);
        _statusType = result.Succeeded ? MessageType.Info : MessageType.Error;
        _statusMessage = result.Succeeded ? "Logged out of GitHub." : "GitHub logout failed.\n" + result.Output;
        if (result.Succeeded)
        {
            _accountName = null;
            _repositoryCheckPending = false;
            _repositoryNameAvailable = null;
            _repositoryCheckMessage = string.Empty;
            _loginTerminalOpened = false;
            _statusType = MessageType.Error;
            _statusMessage = "Not connected.";
        }
    }

    private void CreateRepositoryAndUploadPackage()
    {
        _publishSucceeded = false;
        var showSuccessWindow = false;
        var repositoryUrl = string.Empty;
        var releaseUrl = string.Empty;
        var workingCopyPath = string.Empty;
        var repository = _accountName + "/" + _repositoryName;
        var visibilityNote = _makePublic
            ? "Anyone will be able to access the public repository."
            : "Only people with access to the private repository will be able to access it.";
        if (!EditorUtility.DisplayDialog(
                "Create repository and upload",
                "Upload source from " + _sourcePath + " and attach " + Path.GetFileName(_packagePath) +
                " to a new GitHub Release in " + repository + "?\n\n" +
                visibilityNote +
                "\nThe source folder will be copied; your original files will not be changed.",
                "Create and publish",
                "Cancel"))
        {
            return;
        }

        string stagingPath = null;
        try
        {
            stagingPath = Path.Combine(
                Path.GetTempPath(),
                "GitHubPackagePublisher",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingPath);

            var sourceReadme = FindReadme(_sourcePath);
            var sourceGitignore = Path.Combine(_sourcePath, ".gitignore");
            var sourceLicense = FindLicense(_sourcePath);
            CopySourceDirectory(
                _sourcePath,
                stagingPath,
                _packagePath,
                null,
                null);
            if (_createReadmeIfMissing && sourceReadme == null)
                File.WriteAllText(Path.Combine(stagingPath, "README.md"), CreateReadme(), new UTF8Encoding(false));

            if (_createGitignoreIfMissing && !File.Exists(sourceGitignore))
                File.WriteAllText(Path.Combine(stagingPath, ".gitignore"), CreateUnityGitignore(), new UTF8Encoding(false));
            if (sourceLicense == null && _addMitLicense)
                File.WriteAllText(
                    Path.Combine(stagingPath, "LICENSE"),
                    CreateMitLicense(_copyrightHolder),
                    new UTF8Encoding(false));

            var gitResult = RunCommand(
                "git.exe",
                "init",
                stagingPath,
                30000);
            if (!gitResult.Succeeded)
                throw new InvalidOperationException("Could not initialize the temporary upload repository.\n" + gitResult.Output);

            gitResult = RunCommand("git.exe", "add .", stagingPath, 180000);
            if (!gitResult.Succeeded)
                throw new InvalidOperationException("Could not stage source files.\n" + gitResult.Output);

            var userEmail = _accountName + "@users.noreply.github.com";
            gitResult = RunCommand(
                "git.exe",
                "-c user.name=" + QuoteArgument(_accountName) +
                " -c user.email=" + QuoteArgument(userEmail) +
                " commit -m " + QuoteArgument("Publish " + _repositoryName),
                stagingPath,
                60000);
            if (!gitResult.Succeeded)
                throw new InvalidOperationException("Could not create the upload commit.\n" + gitResult.Output);

            var visibility = _makePublic ? "--public" : "--private";
            var createArguments = "repo create " + QuoteArgument(repository) + " " + visibility +
                                  (string.IsNullOrWhiteSpace(_description)
                                      ? string.Empty
                                      : " --description " + QuoteArgument(_description)) +
                                  " --source " + QuoteArgument(stagingPath) +
                                  " --remote origin --push";
            var result = RunGh(createArguments, 180000);
            if (!result.Succeeded)
                throw new InvalidOperationException("Repository creation or package upload failed.\n" + result.Output);

            var releaseArguments = "release create " + QuoteArgument(_releaseTag) +
                                   " " + QuoteArgument(_packagePath) +
                                   " --repo " + QuoteArgument(repository);
            if (!string.IsNullOrWhiteSpace(_releaseTitle))
                releaseArguments += " --title " + QuoteArgument(_releaseTitle);
            if (!string.IsNullOrWhiteSpace(_releaseNotes))
            {
                releaseArguments += " --notes " + QuoteArgument(_releaseNotes);
            }
            result = RunGh(releaseArguments, 180000);
            if (!result.Succeeded)
            {
                _statusType = MessageType.Error;
                _statusMessage =
                    "Source was uploaded, but creating the GitHub Release failed.\n" +
                    result.Output;
                return;
            }

            repositoryUrl = "https://github.com/" + repository;
            releaseUrl = repositoryUrl + "/releases/tag/" + Uri.EscapeDataString(_releaseTag);
            var completionMessage = "Source and Unity package were published successfully.\n\nRepository: " +
                                    repositoryUrl + "\nRelease: " + releaseUrl;
            if (_createWorkingCopy)
            {
                workingCopyPath = GetWorkingCopyPath();
                Directory.CreateDirectory(_workingCopyParentPath);
                var cloneResult = RunGh(
                    "repo clone " + QuoteArgument(repository) + " " + QuoteArgument(workingCopyPath),
                    180000);
                if (!cloneResult.Succeeded)
                {
                    _statusType = MessageType.Error;
                    _statusMessage =
                        "Repository and Release were published, but the separate local clone failed.\n" +
                        cloneResult.Output;
                    return;
                }

                completionMessage += "\nLocal Git working copy: " + workingCopyPath;
            }

            _statusType = MessageType.Info;
            _publishSucceeded = true;
            _statusMessage = completionMessage;
            showSuccessWindow = true;
        }
        catch (Exception exception)
        {
            _statusType = MessageType.Error;
            _statusMessage = exception.Message;
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingPath) && Directory.Exists(stagingPath))
            {
                var cleanupError = TryDeleteStagingDirectory(stagingPath);
                if (!string.IsNullOrEmpty(cleanupError))
                {
                    _statusType = MessageType.Warning;
                    _statusMessage += "\n" + cleanupError;
                }
            }
        }

        if (showSuccessWindow)
        {
            var localPath = _createWorkingCopy ? workingCopyPath : string.Empty;
            EditorApplication.delayCall += () =>
            {
                if (this != null)
                    GitHubPublishSuccessWindow.Show(repositoryUrl, releaseUrl, localPath);
            };
        }
    }

    private static string TryDeleteStagingDirectory(string path)
    {
        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(directory, FileAttributes.Directory);

            File.SetAttributes(path, FileAttributes.Directory);
            Directory.Delete(path, true);
            return string.Empty;
        }
        catch (Exception exception)
        {
            return "Could not remove temporary upload files at " + path + ": " + exception.Message;
        }
    }

    private string CreateReadme()
    {
        var readme = "# " + _repositoryName + "\n\n";
        if (!string.IsNullOrWhiteSpace(_description))
            readme += _description + "\n\n";
        readme += "## Installation\n\n" +
                  "Download the Unity package from the [Releases](../../releases) page and import it using " +
                  "`Assets > Import Package > Custom Package...`.\n";
        return readme;
    }

    private static void CopySourceDirectory(
        string sourcePath,
        string destinationPath,
        string excludedPackagePath,
        string excludedReadmePath,
        string excludedGitignorePath)
    {
        var excludedPackageFullPath = Path.GetFullPath(excludedPackagePath);
        var excludedReadmeFullPath = string.IsNullOrEmpty(excludedReadmePath)
            ? null
            : Path.GetFullPath(excludedReadmePath);
        CopySourceDirectoryContents(
            Path.GetFullPath(sourcePath),
            Path.GetFullPath(destinationPath),
            excludedPackageFullPath,
            excludedReadmeFullPath,
            string.IsNullOrEmpty(excludedGitignorePath) ? null : Path.GetFullPath(excludedGitignorePath));
    }

    private static void CopySourceDirectoryContents(
        string sourcePath,
        string destinationPath,
        string excludedPackagePath,
        string excludedReadmePath,
        string excludedGitignorePath)
    {
        foreach (var directory in Directory.GetDirectories(sourcePath))
        {
            var directoryInfo = new DirectoryInfo(directory);
            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                IsGeneratedDirectory(directoryInfo.Name))
            {
                continue;
            }

            var targetDirectory = Path.Combine(destinationPath, directoryInfo.Name);
            Directory.CreateDirectory(targetDirectory);
            CopySourceDirectoryContents(
                directory,
                targetDirectory,
                excludedPackagePath,
                excludedReadmePath,
                excludedGitignorePath);
        }

        foreach (var file in Directory.GetFiles(sourcePath))
        {
            var fullPath = Path.GetFullPath(file);
            if (string.Equals(fullPath, excludedPackagePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullPath, excludedReadmePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullPath, excludedGitignorePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(file), ".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(destinationPath, Path.GetFileName(file)));
        }
    }

    private static bool IsGeneratedDirectory(string directoryName)
    {
        return string.Equals(directoryName, ".git", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(directoryName, ".vs", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(directoryName, "Library", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(directoryName, "Temp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(directoryName, "Obj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(directoryName, "Build", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(directoryName, "Builds", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(directoryName, "Logs", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(directoryName, "UserSettings", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateUnityGitignore()
    {
        return "# Unity generated files\n" +
               "[Ll]ibrary/\n" +
               "[Tt]emp/\n" +
               "[Oo]bj/\n" +
               "[Bb]uild/\n" +
               "[Bb]uilds/\n" +
               "[Ll]ogs/\n" +
               "[Uu]ser[Ss]ettings/\n" +
               "*.csproj\n" +
               "*.sln\n" +
               "*.suo\n" +
               "*.user\n" +
               "*.userprefs\n" +
               "*.pidb\n" +
               "*.booproj\n" +
               "*.svd\n" +
               "*.pdb\n" +
               "*.mdb\n" +
               "*.opendb\n" +
               "*.VC.db\n" +
               ".DS_Store\n";
    }

    private GhResult RunGh(string arguments, int timeoutMilliseconds)
    {
        return RunCommand(_ghPath, arguments, null, timeoutMilliseconds);
    }

    private static GhResult RunCommand(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMilliseconds)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var output = new StringBuilder();
            var errors = new StringBuilder();
            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data != null)
                        output.AppendLine(eventArgs.Data);
                };
                process.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data != null)
                        errors.AppendLine(eventArgs.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    process.Kill();
                    return new GhResult(false, "GitHub CLI timed out.");
                }

                process.WaitForExit();
                var resultText = output.ToString().Trim();
                if (errors.Length > 0)
                    resultText += Environment.NewLine + errors.ToString().Trim();
                return new GhResult(process.ExitCode == 0, string.IsNullOrEmpty(resultText)
                    ? "GitHub CLI completed without output."
                    : resultText);
            }
        }
        catch (Exception exception)
        {
            return new GhResult(false, "Could not run GitHub CLI: " + exception.Message);
        }
    }

    private static bool IsRepositoryNameValid(string repositoryName)
    {
        return !string.IsNullOrWhiteSpace(repositoryName) &&
               repositoryName.Length <= 100 &&
               Regex.IsMatch(repositoryName, @"^[A-Za-z0-9._-]+$") &&
               repositoryName[0] != '.' &&
               repositoryName[repositoryName.Length - 1] != '.' &&
               !repositoryName.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindGhPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var candidate = Path.Combine(directory.Trim().Trim('"'), "gh.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GitHub CLI", "gh.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "GitHub CLI",
                "gh.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WinGet",
                "Links",
                "gh.exe")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static string QuoteArgument(string value)
    {
        var escaped = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value ?? string.Empty)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                escaped.Append('\\', backslashes * 2 + 1);
                escaped.Append('"');
                backslashes = 0;
                continue;
            }

            escaped.Append('\\', backslashes);
            escaped.Append(character);
            backslashes = 0;
        }

        escaped.Append('\\', backslashes * 2);
        escaped.Append('"');
        return escaped.ToString();
    }

    private readonly struct GhResult
    {
        internal readonly bool Succeeded;
        internal readonly string Output;

        internal GhResult(bool succeeded, string output)
        {
            Succeeded = succeeded;
            Output = output;
        }
    }
}
