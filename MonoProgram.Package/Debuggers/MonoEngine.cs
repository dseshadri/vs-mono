﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;
using MonoProgram.Package.Debuggers.Events;
using MonoProgram.Package.Utils;
using Renci.SshNet;

namespace MonoProgram.Package.Debuggers
{
    [Guid("D78CF801-CE2A-499B-BF1F-C81742877A34")]
    public class MonoEngine : IDebugEngine2, IDebugProgram3, IDebugEngineLaunch2, IDebugSymbolSettings100
    {
        public SoftDebuggerSession Session { get; private set; }
        public IDebugEventCallback2 Callback { get; private set; }
        public MonoThreadManager ThreadManager { get; private set; }

        private string registryRoot;
        private ushort locale;
        private Guid programId;
        private AD_PROCESS_ID processId;
        private readonly MonoBreakpointManager breakpointManager;
        private AutoResetEvent waiter;
        private MonoDebuggerSettings settings;
        private SshCommand runCommand;
        private Connection connection;
        private IVsOutputWindowPane outputWindow;

        public MonoEngine()
        {
            breakpointManager = new MonoBreakpointManager(this);
            ThreadManager = new MonoThreadManager(this);
        }

        ~MonoEngine()
        {
            runCommand?.Dispose();
        }

        public string TranslateToBuildServerPath(string localPath)
        {
            var localRoot = settings.SourceRoot;
            var documentName = localPath.Replace('\\', '/');
            var remoteRoot = settings.BuildRoot;
            documentName = remoteRoot + documentName.Substring(localRoot.Length);
            return documentName;
        }

        public string TranslateToLocalPath(string buildServerPath)
        {
            var localRoot = settings.SourceRoot;
            var documentName = buildServerPath.Replace('/', '\\');
            var remoteRoot = settings.BuildRoot;
            documentName = localRoot + documentName.Substring(remoteRoot.Length);
            return documentName;
        }

        public string GetDocumentName(IntPtr locationPtr)
        {
            TEXT_POSITION[] startPosition;
            TEXT_POSITION[] endPosition;
            return GetLocationInfo(locationPtr, out startPosition, out endPosition);
        }

        public string GetLocationInfo(IntPtr locationPtr, out TEXT_POSITION[] startPosition, out TEXT_POSITION[] endPosition)
        {
            var docPosition = (IDebugDocumentPosition2)Marshal.GetObjectForIUnknown(locationPtr);
            var result = GetLocationInfo(docPosition, out startPosition, out endPosition);
            Marshal.ReleaseComObject(docPosition);
            return result;
        }

        public string GetLocationInfo(IDebugDocumentPosition2 docPosition, out TEXT_POSITION[] startPosition, out TEXT_POSITION[] endPosition)
        {
            string documentName;
            EngineUtils.CheckOk(docPosition.GetFileName(out documentName));

            startPosition = new TEXT_POSITION[1];
            endPosition = new TEXT_POSITION[1];
            EngineUtils.CheckOk(docPosition.GetRange(startPosition, endPosition));

            return documentName;
        }

        public int EnumPrograms(out IEnumDebugPrograms2 ppEnum)
        {
            throw new NotImplementedException();
        }

        public int CreatePendingBreakpoint(IDebugBreakpointRequest2 request, out IDebugPendingBreakpoint2 pendingBreakpoint)
        {
            pendingBreakpoint = new MonoPendingBreakpoint(breakpointManager, request);

            return VSConstants.S_OK;
        }

        public int SetException(EXCEPTION_INFO[] pException)
        {
            throw new NotImplementedException();
        }

        public int RemoveSetException(EXCEPTION_INFO[] pException)
        {
            throw new NotImplementedException();
        }

        public int RemoveAllSetExceptions(ref Guid guidType)
        {
            throw new NotImplementedException();
        }

        public int GetEngineId(out Guid engineGuid)
        {
            engineGuid = new Guid(Guids.EngineId);
            return VSConstants.S_OK;
        }

        public int DestroyProgram(IDebugProgram2 pProgram)
        {
            throw new NotImplementedException();
        }

        public int ContinueFromSynchronousEvent(IDebugEvent2 @event)
        {
            if (@event is MonoProgramDestroyEvent)
            {
                Session.Dispose();
            }
            return VSConstants.S_OK;
        }

        public int SetLocale(ushort languageId)
        {
            this.locale = languageId;
            return VSConstants.S_OK;
        }

        public int SetRegistryRoot(string registryRoot)
        {
            this.registryRoot = registryRoot;
            return VSConstants.S_OK;
        }

        public int SetMetric(string metric, object value)
        {
            return VSConstants.S_OK;
        }

        public int CauseBreak()
        {
            EventHandler<TargetEventArgs> stepFinished = null;
            stepFinished = (sender, args) =>
            {
                Session.TargetStopped -= stepFinished;
                Send(new MonoBreakpointEvent(new MonoBoundBreakpointsEnum(new IDebugBoundBreakpoint2[0])), MonoStepCompleteEvent.IID, ThreadManager[args.Thread]);
            };
            Session.TargetStopped += stepFinished;

            Session.Stop();
            return VSConstants.S_OK;
        }

        int IDebugProgram2.EnumThreads(out IEnumDebugThreads2 ppEnum)
        {
            var threads = ThreadManager.All.ToArray();

            var threadObjects = new MonoThread[threads.Length];
            for (int i = 0; i < threads.Length; i++)
            {
                threadObjects[i] = threads[i];
            }

            ppEnum = new MonoThreadEnum(threadObjects);
            
            return VSConstants.S_OK;
        }

        int IDebugProgram2.GetName(out string programName)
        {
            programName = null;
            return VSConstants.S_OK;
        }

        int IDebugProgram2.GetProcess(out IDebugProcess2 ppProcess)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram2.Terminate()
        {
            return VSConstants.S_OK;
        }

        int IDebugProgram2.Attach(IDebugEventCallback2 pCallback)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram2.CanDetach()
        {
            return VSConstants.S_OK;
        }

        int IDebugProgram2.Detach()
        {
            if (!Session.IsRunning)
            {
                Session.Continue();
            }
            Session.Dispose();
            return VSConstants.S_OK;
        }

        int IDebugProgram2.GetProgramId(out Guid programId)
        {
            programId = this.programId;
            return VSConstants.S_OK;
        }

        int IDebugProgram2.GetDebugProperty(out IDebugProperty2 ppProperty)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram2.Execute()
        {
            throw new NotImplementedException();
        }

        public int Step(IDebugThread2 thread, enum_STEPKIND kind, enum_STEPUNIT unit)
        {
            switch (kind)
            {
                case enum_STEPKIND.STEP_BACKWARDS:
                    return VSConstants.E_NOTIMPL;
            }

            EventHandler<TargetEventArgs> stepFinished = null;
            stepFinished = (sender, args) =>
            {
                Session.TargetStopped -= stepFinished;
                Send(new MonoStepCompleteEvent(), MonoStepCompleteEvent.IID, ThreadManager[args.Thread]);
            };
            Session.TargetStopped += stepFinished;

            switch (kind)
            {
                case enum_STEPKIND.STEP_OVER:
                    switch (unit)
                    {
                        case enum_STEPUNIT.STEP_INSTRUCTION:
                            Session.NextInstruction();
                            break;
                        default:
                            Session.NextLine();
                            break;
                    }
                    break;
                case enum_STEPKIND.STEP_INTO:
                    switch (unit)
                    {
                        case enum_STEPUNIT.STEP_INSTRUCTION:
                            Session.StepInstruction();
                            break;
                        default:
                            Session.StepLine();
                            break;
                    }
                    break;
                case enum_STEPKIND.STEP_OUT:
                    Session.Finish();
                    break;
            }
            return VSConstants.S_OK;
        }

        int IDebugProgram2.GetEngineInfo(out string pbstrEngine, out Guid pguidEngine)
        {
            throw new NotImplementedException();
        }

        public int EnumCodeContexts(IDebugDocumentPosition2 pDocPos, out IEnumDebugCodeContexts2 ppEnum)
        {
            TEXT_POSITION[] startPosition;
            TEXT_POSITION[] endPosition;
            var documentName = breakpointManager.Engine.GetLocationInfo(pDocPos, out startPosition, out endPosition);

            var textPosition = new TEXT_POSITION { dwLine = startPosition[0].dwLine + 1 };
            var documentContext = new MonoDocumentContext(documentName, textPosition, textPosition, null);
            ppEnum = new MonoCodeContextEnum(new[] { new MonoMemoryAddress(this, 0, documentContext) });
            return VSConstants.S_OK;
        }

        int IDebugProgram2.GetMemoryBytes(out IDebugMemoryBytes2 ppMemoryBytes)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram2.GetDisassemblyStream(enum_DISASSEMBLY_STREAM_SCOPE dwScope, IDebugCodeContext2 pCodeContext, out IDebugDisassemblyStream2 ppDisassemblyStream)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram2.EnumModules(out IEnumDebugModules2 ppEnum)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram2.GetENCUpdate(out object ppUpdate)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram2.EnumCodePaths(string pszHint, IDebugCodeContext2 pStart, IDebugStackFrame2 pFrame, int fSource, out IEnumCodePaths2 ppEnum, out IDebugCodeContext2 ppSafety)
        {
            ppEnum = null;
            ppSafety = null;
            return VSConstants.E_NOTIMPL;
        }

        int IDebugProgram2.WriteDump(enum_DUMPTYPE DUMPTYPE, string pszDumpUrl)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.EnumThreads(out IEnumDebugThreads2 ppEnum)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.GetName(out string pbstrName)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.GetProcess(out IDebugProcess2 ppProcess)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.Terminate()
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.Attach(IDebugEventCallback2 pCallback)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.CanDetach()
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.Detach()
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.GetProgramId(out Guid pguidProgramId)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.GetDebugProperty(out IDebugProperty2 ppProperty)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.Execute()
        {
            throw new NotImplementedException();
        }

        public int Continue(IDebugThread2 pThread)
        {
            Session.Continue();
            return VSConstants.S_OK;
        }

        int IDebugProgram3.GetEngineInfo(out string pbstrEngine, out Guid pguidEngine)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.GetMemoryBytes(out IDebugMemoryBytes2 ppMemoryBytes)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.GetDisassemblyStream(enum_DISASSEMBLY_STREAM_SCOPE dwScope, IDebugCodeContext2 pCodeContext, out IDebugDisassemblyStream2 ppDisassemblyStream)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.EnumModules(out IEnumDebugModules2 ppEnum)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.GetENCUpdate(out object ppUpdate)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.EnumCodePaths(string pszHint, IDebugCodeContext2 pStart, IDebugStackFrame2 pFrame, int fSource, out IEnumCodePaths2 ppEnum, out IDebugCodeContext2 ppSafety)
        {
            throw new NotImplementedException();
        }

        int IDebugProgram3.WriteDump(enum_DUMPTYPE DUMPTYPE, string pszDumpUrl)
        {
            throw new NotImplementedException();
        }

        public int ExecuteOnThread(IDebugThread2 pThread)
        {
            var monoThread = (MonoThread)pThread;
            var thread = monoThread.GetDebuggedThread();
            if (Session.ActiveThread?.Id != thread.Id) 
                thread.SetActive();
            Session.Continue();
            return VSConstants.S_OK;
        }

        public void Log(string message)
        {
            outputWindow.OutputString(message + "\r\n");
        }

        public int LaunchSuspended(string server, IDebugPort2 port, string exe, string args, string directory, string environment, string options, enum_LAUNCH_FLAGS launchFlags, uint standardInput, uint standardOutput, uint standardError, IDebugEventCallback2 callback, out IDebugProcess2 process)
        {
            waiter = new AutoResetEvent(false);
            settings = MonoDebuggerSettings.Parse(options);

            Task.Run(() =>
            {
                var outputWindow = (IVsOutputWindow)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsOutputWindow));
                var generalPaneGuid = VSConstants.GUID_OutWindowDebugPane;
                outputWindow.GetPane(ref generalPaneGuid, out this.outputWindow);

                // Upload the contents of the output folder to the target directory
                if (connection == null)
                {
                    connection = ConnectionManager.Get(settings);
                    if (!connection.IsConnected)
                    {
                        Log("Connecting SSH and SFTP...");
                        connection.Connect();
                        Log("Connected");
                    }
                }

                Log("Uploading program...");

                // Ensure target directory exists:
                var targetDirectories = directory.Split('/');
                foreach (var part in targetDirectories)
                {
                    if (!connection.Sftp.Exists(part))
                        connection.Sftp.CreateDirectory(part);
                    connection.Sftp.ChangeDirectory(part);
                }
                foreach (var _ in targetDirectories)
                {
                    connection.Sftp.ChangeDirectory("..");
                }

                var outputDirectory = new DirectoryInfo(Path.GetDirectoryName(exe));
                foreach (var file in outputDirectory.EnumerateFiles().Where(x => x.Extension == ".dll" || x.Extension == ".mdb" || x.Extension == ".exe"))
                {
                    using (var stream = file.OpenRead())
                    {
                        connection.Sftp.UploadFile(stream, $"{directory}/{file.Name}", true);
                    }
                    Log($"Uploaded {file.FullName}");
                }
                Log("Done");

                var targetExe = directory + "/" + Path.GetFileName(exe);
                connection.Ssh.RunCommand("cd ~");
                Log("Launching application");
                var commandText = $"mono --debug=mdb-optimizations --debugger-agent=transport=dt_socket,address=0.0.0.0:6438,server=y {targetExe}";
                connection.Ssh.RunCommand("kill $(ps auxww | grep '" + commandText + "' | awk '{print $2}')", this.outputWindow);
                runCommand = connection.Ssh.BeginCommand(commandText, this.outputWindow, ar =>
                {
// Persistent connection
//                    sshClient.Disconnect();
//                    sshClient.Dispose();
                });

                // Trigger that the app is now running for whomever might be waiting for that signal
                waiter.Set();
            });

            Session = new SoftDebuggerSession();
            Session.TargetReady += (sender, eventArgs) =>
            {
                var activeThread = Session.ActiveThread;
                ThreadManager.Add(activeThread, new MonoThread(this, activeThread));
//                Session.Stop();
            };
            Session.ExceptionHandler = exception => true;
            Session.TargetExceptionThrown += (sender, x) => Console.WriteLine(x.Type);
            Session.TargetExited += (sender, x) => Send(new MonoProgramDestroyEvent((uint?)x.ExitCode ?? 0), MonoProgramDestroyEvent.IID, null);
            Session.TargetUnhandledException += (sender, x) => Console.WriteLine(x.Type);
            Session.LogWriter = (stderr, text) => Console.WriteLine(text);
            Session.OutputWriter = (stderr, text) => Console.WriteLine(text);
            Session.TargetThreadStarted += (sender, x) => ThreadManager.Add(x.Thread, new MonoThread(this, x.Thread));
            Session.TargetThreadStopped += (sender, x) => ThreadManager.Remove(x.Thread);
            Session.TargetStopped += (sender, x) => Console.WriteLine(x.Type);
            Session.TargetStarted += (sender, x) => Console.WriteLine();
            Session.TargetSignaled += (sender, x) => Console.WriteLine(x.Type);
            Session.TargetInterrupted += (sender, x) => Console.WriteLine(x.Type);
            Session.TargetHitBreakpoint += (sender, x) =>
            {
                var breakpoint = x.BreakEvent as Breakpoint;
                var pendingBreakpoint = breakpointManager[breakpoint];
                Send(new MonoBreakpointEvent(new MonoBoundBreakpointsEnum(pendingBreakpoint.BoundBreakpoints)), MonoBreakpointEvent.IID, ThreadManager[x.Thread]);
            };

            processId = new AD_PROCESS_ID();
            processId.ProcessIdType = (uint)enum_AD_PROCESS_ID.AD_PROCESS_ID_GUID;
            processId.guidProcessId = Guid.NewGuid();

            EngineUtils.CheckOk(port.GetProcess(processId, out process));
            Callback = callback;

            return VSConstants.S_OK;
        }

        public int ResumeProcess(IDebugProcess2 process)
        {
            IDebugPort2 port;
            EngineUtils.RequireOk(process.GetPort(out port));
            
            IDebugDefaultPort2 defaultPort = (IDebugDefaultPort2)port;
            IDebugPortNotify2 portNotify;
            EngineUtils.RequireOk(defaultPort.GetPortNotify(out portNotify));

            EngineUtils.RequireOk(portNotify.AddProgramNode(new MonoProgramNode(processId)));

            return VSConstants.S_OK;
        }

        public int Attach(IDebugProgram2[] programs, IDebugProgramNode2[] rgpProgramNodes, uint celtPrograms, IDebugEventCallback2 pCallback, enum_ATTACH_REASON dwReason)
        {
            var program = programs[0];
            IDebugProcess2 process;
            program.GetProcess(out process);
            Guid processId;
            process.GetProcessId(out processId);
            if (processId != this.processId.guidProcessId)
                return VSConstants.S_FALSE;

            EngineUtils.RequireOk(program.GetProgramId(out programId));

            Task.Run(() =>
            {
                waiter.WaitOne();

                var ipAddress = HostUtils.ResolveHostOrIPAddress(settings.Host);
                Session.Run(new SoftDebuggerStartInfo(new SoftDebuggerConnectArgs("", ipAddress, 6438)), 
                    new DebuggerSessionOptions { EvaluationOptions = EvaluationOptions.DefaultOptions, ProjectAssembliesOnly = false });
            });

            MonoEngineCreateEvent.Send(this);
            MonoProgramCreateEvent.Send(this);                

            return VSConstants.S_OK;
        }

        public int CanTerminateProcess(IDebugProcess2 pProcess)
        {
            return VSConstants.S_OK;
        }

        public int TerminateProcess(IDebugProcess2 pProcess)
        {
            pProcess.Terminate();
            Send(new MonoProgramDestroyEvent(0), MonoProgramDestroyEvent.IID, null);
            return VSConstants.S_OK;
        }

        public int SetSymbolLoadState(int isManual, int loadAdjacentSymbols, string includeList, string excludeList)
        {
            return VSConstants.S_OK;
        }

        public void Send(IDebugEvent2 eventObject, string iidEvent, IDebugProgram2 program, IDebugThread2 thread)
        {
            Callback.Send(this, eventObject, iidEvent, program, thread);
        }

        public void Send(IDebugEvent2 eventObject, string iidEvent, IDebugThread2 thread)
        {
            Send(eventObject, iidEvent, this, thread);
        }
    }
}