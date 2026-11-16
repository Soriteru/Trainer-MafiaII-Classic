Option Explicit
Dim shell, fs, projectRoot, dotnetPath, appPath, command
Set shell = CreateObject("WScript.Shell")
Set fs = CreateObject("Scripting.FileSystemObject")
projectRoot = fs.GetParentFolderName(WScript.ScriptFullName)
dotnetPath = shell.ExpandEnvironmentStrings("%ProgramFiles%") & "\dotnet\dotnet.exe"
appPath = projectRoot & "\app\EmpireBayTrainer.dll"
If Not fs.FileExists(dotnetPath) Or Not fs.FileExists(appPath) Then
  MsgBox "Le runtime .NET ou les fichiers du trainer sont introuvables.", 16, "Empire Bay Trainer"
  WScript.Quit 1
End If
shell.CurrentDirectory = projectRoot
command = Chr(34) & dotnetPath & Chr(34) & " " & Chr(34) & appPath & Chr(34)
shell.Run command, 0, False
