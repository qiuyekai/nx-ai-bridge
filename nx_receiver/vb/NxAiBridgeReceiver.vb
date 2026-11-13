' NX AI Bridge - receiver journal, VB.NET fallback loader (ALPHA).
'
' Use this file ONLY if your NX refuses to play the C# journal ..\NxAiBridgeReceiver.cs.
' It does exactly the same: it compiles the receiver kernel from
'   %USERPROFILE%\.nx-ai-bridge\nx_receiver\kernel\*.cs
' and starts it. In NX: Alt+F8 (Tools > Journal > Play...), select this file, Run.
Option Strict Off
Imports System
Imports System.CodeDom.Compiler
Imports System.Collections.Generic
Imports System.IO
Imports System.Reflection
Imports System.Security.Cryptography
Imports System.Text

Module NxAiBridgeReceiver
    Private Const DefaultMode As String = "--live"

    Sub Main(ByVal args() As String)
        Dim home As String = Environment.GetEnvironmentVariable("NX_AI_BRIDGE_HOME")
        If String.IsNullOrEmpty(home) Then
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nx-ai-bridge")
        End If
        Try
            Dim all As New List(Of String)
            Dim hasMode As Boolean = False
            If args IsNot Nothing Then
                For i As Integer = 0 To args.Length - 1
                    all.Add(args(i))
                    If args(i) = "--home" AndAlso i + 1 < args.Length Then home = args(i + 1)
                    If args(i) = "--live" OrElse args(i) = "--modal" OrElse args(i) = "--console" OrElse args(i) = "--batch" _
                        OrElse args(i) = "--selfcheck" OrElse args(i) = "--compile-check" Then hasMode = True
                Next
            End If
            If Not hasMode Then all.Add(DefaultMode)
            all.Add("--home")
            all.Add(home)
            Dim kernel As Assembly = LoadKernel(home)
            Dim entry As Type = kernel.GetType("NxAiBridge.Entry", True)
            entry.GetMethod("Run").Invoke(Nothing, New Object() {all.ToArray()})
        Catch ex As Exception
            Report(home, ex)
        End Try
    End Sub

    Private Function LoadKernel(ByVal home As String) As Assembly
        Dim kernelDir As String = Path.Combine(Path.Combine(home, "nx_receiver"), "kernel")
        If Not Directory.Exists(kernelDir) Then
            Throw New Exception("Receiver files not found in " & kernelDir & ". Run install.ps1 (or: nx-ai-bridge install-receiver) first.")
        End If
        Dim files() As String = Directory.GetFiles(kernelDir, "*.cs")
        Array.Sort(files, StringComparer.OrdinalIgnoreCase)
        If files.Length = 0 Then Throw New Exception("No kernel source files in " & kernelDir)

        Dim text As New StringBuilder()
        For Each f As String In files
            text.Append(Path.GetFileName(f)).Append(vbLf).Append(File.ReadAllText(f)).Append(vbLf)
        Next
        Dim hash As String
        Using hasher As MD5 = MD5.Create()
            Dim h() As Byte = hasher.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))
            Dim sb As New StringBuilder()
            For i As Integer = 0 To 5
                sb.Append(h(i).ToString("x2"))
            Next
            hash = sb.ToString()
        End Using
        Dim name As String = "NxAiBridgeKernel-" & hash

        For Each loaded As Assembly In AppDomain.CurrentDomain.GetAssemblies()
            Try
                If loaded.GetName().Name = name Then Return loaded
            Catch
            End Try
        Next
        Dim cache As String = Path.Combine(home, "cache")
        If Not Directory.Exists(cache) Then Directory.CreateDirectory(cache)
        Dim dll As String = Path.Combine(cache, name & ".dll")
        If File.Exists(dll) Then
            Try
                Return Assembly.LoadFrom(dll)
            Catch
                Try
                    File.Delete(dll)
                Catch
                End Try
            End Try
        End If

        Dim cp As New CompilerParameters()
        cp.GenerateExecutable = False
        cp.GenerateInMemory = False
        cp.OutputAssembly = dll
        cp.TempFiles = New TempFileCollection(cache, False)
        cp.ReferencedAssemblies.Add("System.dll")
        cp.ReferencedAssemblies.Add("System.Core.dll")
        cp.ReferencedAssemblies.Add("System.Windows.Forms.dll")
        cp.ReferencedAssemblies.Add("System.Drawing.dll")
        Dim provider As New Microsoft.CSharp.CSharpCodeProvider()
        Dim results As CompilerResults = provider.CompileAssemblyFromFile(cp, files)
        Dim errors As New StringBuilder()
        For Each e As CompilerError In results.Errors
            If e.IsWarning Then Continue For
            errors.Append(Path.GetFileName(e.FileName)).Append("(").Append(e.Line).Append(",").Append(e.Column).Append("): ")
            errors.Append(e.ErrorNumber).Append(": ").Append(e.ErrorText).Append(Environment.NewLine)
        Next
        If errors.Length > 0 Then Throw New Exception("The receiver kernel does not compile:" & Environment.NewLine & errors.ToString())
        Return results.CompiledAssembly
    End Function

    Private Sub Report(ByVal home As String, ByVal ex As Exception)
        While TypeOf ex Is TargetInvocationException AndAlso ex.InnerException IsNot Nothing
            ex = ex.InnerException
        End While
        Dim text As String = "NX AI Bridge could not start." & Environment.NewLine & ex.GetType().FullName & ": " & ex.Message _
            & Environment.NewLine & ex.StackTrace & Environment.NewLine
        Try
            Dim logs As String = Path.Combine(home, "logs")
            If Not Directory.Exists(logs) Then Directory.CreateDirectory(logs)
            File.WriteAllText(Path.Combine(logs, "loader-error.txt"), text)
            text &= Environment.NewLine & "(saved to " & Path.Combine(logs, "loader-error.txt") & ")"
        Catch
        End Try
        Try
            Console.Error.WriteLine(text)
        Catch
        End Try
        Try
            Dim session As NXOpen.Session = NXOpen.Session.GetSession()
            session.ListingWindow.Open()
            session.ListingWindow.WriteLine(text)
        Catch
        End Try
    End Sub

    ' Keep the compiled journal loaded after Main() returns: the control window lives on.
    Public Function GetUnloadOption(ByVal dummy As String) As Integer
        Return CInt(NXOpen.Session.LibraryUnloadOption.AtTermination)
    End Function
End Module
