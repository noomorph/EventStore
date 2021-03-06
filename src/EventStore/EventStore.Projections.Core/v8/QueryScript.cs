// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace EventStore.Projections.Core.v8
{
    class QueryScript : IDisposable
    {
        private readonly CompiledScript _script;
        private readonly Dictionary<string, IntPtr> _registeredHandlers = new Dictionary<string, IntPtr>();

        private Func<string, string[], string> _getStatePartition;
        private Action<string, string[]> _processEvent;
        private Func<string, string[], string> _testArray;
        private Func<string> _getState;
        private Action<string> _setState;
        private Action _initialize;
        private Func<string> _getStatistics;
        private Func<string> _getSources;

        // the following two delegates must be kept alive while used by unmanaged code
        private readonly Js1.CommandHandlerRegisteredDelegate _commandHandlerRegisteredCallback; // do not inline
        private readonly Js1.ReverseCommandHandlerDelegate _reverseCommandHandlerDelegate; // do not inline
        private QuerySourcesDefinition _sources;
        private Exception _reverseCommandHandlerException;

        public event Action<string> Emit;

        public QueryScript(PreludeScript prelude, string script, string fileName)
        {
            _commandHandlerRegisteredCallback = CommandHandlerRegisteredCallback;
            _reverseCommandHandlerDelegate = ReverseCommandHandler;

            _script = CompileScript(prelude, script, fileName);

            try
            {
                GetSources();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private CompiledScript CompileScript(PreludeScript prelude, string script, string fileName)
        {
            IntPtr query = Js1.CompileQuery(
                prelude.GetHandle(), script, fileName, _commandHandlerRegisteredCallback, _reverseCommandHandlerDelegate);
            CompiledScript.CheckResult(query, disposeScriptOnException: true);
            return new CompiledScript(query, fileName);
        }

        private void ReverseCommandHandler(string commandName, string commandBody)
        {
            try
            {
                switch (commandName)
                {
                    case "emit":
                        DoEmit(commandBody);
                        break;
                    default:
                        Console.WriteLine("Ignoring unknown reverse command: '{0}'", commandName);
                        break;
                }
            }
            catch (Exception ex)
            {
                // report only the first exception occured in reverse command handler
                if (_reverseCommandHandlerException == null)
                    _reverseCommandHandlerException = ex;
            }
        }

        private void CommandHandlerRegisteredCallback(string commandName, IntPtr handlerHandle)
        {
            _registeredHandlers.Add(commandName, handlerHandle);
            //TODO: change to dictionary
            switch (commandName)
            {
                case "initialize":
                    _initialize = () => ExecuteHandler(handlerHandle, "");
                    break;
                case "get_state_partition":
                    _getStatePartition = (json, other) => ExecuteHandler(handlerHandle, json, other);
                    break;
                case "process_event":
                    _processEvent = (json, other) => ExecuteHandler(handlerHandle, json, other);
                    break;
                case "test_array":
                    _testArray = (json, other) => ExecuteHandler(handlerHandle, json, other);
                    break;
                case "get_state":
                    _getState = () => ExecuteHandler(handlerHandle, "");
                    break;
                case "set_state":
                    _setState = json => ExecuteHandler(handlerHandle, json);
                    break;
                case "get_statistics":
                    _getStatistics = () => ExecuteHandler(handlerHandle, "");
                    break;
                case "get_sources":
                    _getSources = () => ExecuteHandler(handlerHandle, "");
                    break;
                case "set_debugging":
                    // ignore - browser based debugging only
                    break;
                default:
                    Console.WriteLine(
                        string.Format("Unknown command handler registered. Command name: {0}", commandName));
                    break;
            }
        }

        private void DoEmit(string commandBody)
        {
            OnEmit(commandBody);
        }

        private void GetSources()
        {
            if (_getSources == null)
                throw new InvalidOperationException("'get_sources' command handler has not been registered");
            var sourcesJson = _getSources();

            Console.WriteLine(sourcesJson);

            _sources = sourcesJson.ParseJson<QuerySourcesDefinition>();

            if (_sources.AllStreams)
                Console.WriteLine("All streams requested");
            else
            {
                foreach (var category in _sources.Categories)
                    Console.WriteLine("Category {0} requested", category);
                foreach (var stream in _sources.Streams)
                    Console.WriteLine("Stream {0} requested", stream);
            }
            if (_sources.AllEvents)
                Console.WriteLine("All events requested");
            else
            {
                foreach (var @event in _sources.Events)
                    Console.WriteLine("Event {0} requested", @event);
            }
        }

        private string ExecuteHandler(IntPtr commandHandlerHandle, string json, string[] other = null)
        {
            _reverseCommandHandlerException = null;
            IntPtr resultJsonPtr;
            IntPtr resultHandle = Js1.ExecuteCommandHandler(
                _script.GetHandle(), commandHandlerHandle, json, other, other != null ? other.Length : 0,
                out resultJsonPtr);
            if (resultHandle == IntPtr.Zero)
                CompiledScript.CheckResult(_script.GetHandle(), disposeScriptOnException: false);
            //TODO: do we need to free resulktJsonPtr in case of exception thrown a line above
            string resultJson = Marshal.PtrToStringUni(resultJsonPtr);
            Js1.FreeResult(resultHandle);
            if (_reverseCommandHandlerException != null)
            {
                throw new ApplicationException(
                    "An exception occurred while executing a reverse command handler. " + _reverseCommandHandlerException.Message,
                    _reverseCommandHandlerException);
            }
            return resultJson;
        }

        private void OnEmit(string obj)
        {
            Action<string> handler = Emit;
            if (handler != null) handler(obj);
        }

        public void Dispose()
        {
            _script.Dispose();
        }

        public void Initialize()
        {
            InitializeScript();
        }

        private void InitializeScript()
        {
            if (_initialize != null)
                _initialize();
        }

        public string GetPartition(string json, string[] other)
        {
            if (_getStatePartition == null)
                throw new InvalidOperationException("'get_state_partition' command handler has not been registered");

            return _getStatePartition(json, other);
        }

        public void Push(string json, string[] other)
        {
            if (_processEvent == null)
                throw new InvalidOperationException("'process_event' command handler has not been registered");

            _processEvent(json, other);
        }

        public string GetState()
        {
            if (_getState == null)
                throw new InvalidOperationException("'get_state' command handler has not been registered");
            return _getState();
        }

        public void SetState(string state)
        {
            if (_setState == null)
                throw new InvalidOperationException("'set_state' command handler has not been registered");
            _setState(state);
        }

        public string GetStatistics()
        {
            if (_getState == null)
                throw new InvalidOperationException("'get_statistics' command handler has not been registered");
            return _getStatistics();
        }

        public QuerySourcesDefinition GetSourcesDefintion()
        {
            return _sources;
        }

        [DataContract]
        internal class QuerySourcesDefinition
        {
            [DataMember(Name = "all_streams")]
            public bool AllStreams { get; set; }

            [DataMember(Name = "categories")]
            public string[] Categories { get; set; }

            [DataMember(Name = "streams")]
            public string[] Streams { get; set; }

            [DataMember(Name = "all_events")]
            public bool AllEvents { get; set; }

            [DataMember(Name = "events")]
            public string[] Events { get; set; }

            [DataMember(Name = "by_streams")]
            public bool ByStreams { get; set; }

            [DataMember(Name = "by_custom_partitions")]
            public bool ByCustomPartitions { get; set; }

            [DataMember(Name = "options")]
            public QuerySourcesDefinitionOptions Options { get; set;}
        }

        [DataContract]
        internal class QuerySourcesDefinitionOptions
        {
            [DataMember(Name = "stateStreamName")]
            public string StateStreamName { get; set; }

            [DataMember(Name = "useEventIndexes")]
            public bool UseEventIndexes { get; set; }

            [DataMember(Name = "$forceProjectionName")]
            public string ForceProjectionName { get; set; }

            [DataMember(Name = "reorderEvents")]
            public bool ReorderEvents { get; set; }

            [DataMember(Name = "processingLag")]
            public int? ProcessingLag { get; set; }

            [DataMember(Name = "emitStateUpdated")]
            public bool EmitStateUpdated { get; set; }


        }
    }
}
