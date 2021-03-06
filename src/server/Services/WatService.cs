using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Ahghee;
using Ahghee.Grpc;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using cli;
using cli.antlr;
using cli_grammer;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Utils = Ahghee.Utils;

namespace server
{
    
    public class WatService : Ahghee.Grpc.WatDbService.WatDbServiceBase
    {
        private readonly ILogger<WatService> _logger;
        private readonly IStorage _db;
        private TimeSpan _procTotalProcessorTime;
        private DateTime _dateTime;
        private Process _proc;

        public WatService(ILogger<WatService> logger, IStorage db)
        {
            _logger = logger;
            _db = db;

            // these are used to keep track of cpu.
            _proc = Process.GetCurrentProcess();
            _procTotalProcessorTime = _proc.TotalProcessorTime;
            _dateTime = DateTime.UtcNow;
        }

        static UnbufferedTokenStream makeAHGHEEStream(string text)
        {
            var sr = new StringReader(text);
            var ins = new AntlrInputStream(sr);
            var lex = new AHGHEELexer(ins);
            return new UnbufferedTokenStream(lex);
        }
        static UnbufferedTokenStream makeNTRIPLESStream(Stream text)
        {
            var ins = new AntlrInputStream(text);
            var lex = new NTRIPLESLexer(ins);
            return new UnbufferedTokenStream(lex);
        }
        static UnbufferedTokenStream makeNTRIPLESStream(TextReader text)
        {
            var ins = new AntlrInputStream(text);
            var lex = new NTRIPLESLexer(ins);
            return new UnbufferedTokenStream(lex);
        }
        public override async Task<GetMetricsResponse> GetMetrics(GetMetricsRequest request, ServerCallContext context)
        {
            return await _db.GetMetrics(request,context.CancellationToken);
            var cpuPercent = UpdateCpuPercent();
        }

        private float UpdateCpuPercent()
        {
            var newnow = DateTime.UtcNow;
            var elapsed = (newnow - _dateTime).TotalMilliseconds;
            var newcpu = _proc.TotalProcessorTime;
            var cputime = (newcpu - _procTotalProcessorTime).TotalMilliseconds;
            var cpuPercent = Convert.ToSingle(cputime / (Environment.ProcessorCount * elapsed));
            _dateTime = newnow;
            _procTotalProcessorTime = newcpu;
            return cpuPercent;
        }

        private int ParseForNewLine(ReadOnlySpan<byte> data)
        {
            var newLinePos =0;
            for (int i = 0; i < data.Length; i++)
            {
                var c = (char) data[i];
            
                if (c == '\n' )
                {
                    newLinePos = i;   
                }
            }

            return newLinePos;
        }
        private async Task<int> ParseNTStreamNoTree(Stream data)
        {
            ConcurrentQueue<Node> batch = new ConcurrentQueue<Node>();

            var parser = new NTRIPLESParser(makeNTRIPLESStream(data));
            //parser.TrimParseTree = true;
            parser.BuildParseTree = true;
            int lastValidPosition = 0;
            parser.AddErrorListener(new ErrorListener());
            NTRIPLESParser.TripleContext cc = null;
            
            for (;; cc = parser.triple())
            {
                if (cc?.exception != null
                    //&& cc.exception.GetType() != typeof(Antlr4.Runtime.InputMismatchException)
                    //&& cc.exception.GetType() != typeof(Antlr4.Runtime.NoViableAltException)
                )
                {
                    Console.WriteLine(cc.exception.Message);
                    Console.WriteLine(
                        $"found {cc.exception.OffendingToken.Text} at Line {cc.exception.OffendingToken.Line} offset at {cc.exception.OffendingToken.StartIndex}");
                }

                if (cc != null)
                {
                    lastValidPosition = cc.Start.StartIndex;
                    
                }
                //lastValidPosition = parser.CurrentToken.StopIndex;                
                if (parser.CurrentToken.Type == TokenConstants.Eof)
                {
                    break;
                }
            }

            return lastValidPosition;
        }
        
        private async Task ParseNTStream(Stream data)
        {
            ConcurrentQueue<Node> batch = new ConcurrentQueue<Node>();

            var parser = new NTRIPLESParser(makeNTRIPLESStream(data));
            parser.BuildParseTree = true;

            async Task GroupAndAdd(List<Node> list)
            {
                var nodes = list.GroupBy(n => n.Id, (key, ns) =>
                {
                    return new Node
                    {
                        Id = key,
                        Attributes = {ns.SelectMany(_n => _n.Attributes)}
                    };
                });
                await _db.Add(nodes.ToList());
            }

            parser.AddParseListener(new NtriplesListener(async (node) =>
            {
                batch.Enqueue(node);

                // because we dealing with triples, we may get a bunch for the same nodeId
                // do a little grouping to reduce the amount of fragments we create
                if (batch.Count <= 600) return;
                var mine = new List<Node>();
                while (!batch.IsEmpty)
                {
                    if (batch.TryDequeue(out var nnnnnn))
                    {
                        mine.Add(nnnnnn);    
                    }
                }
                
                await GroupAndAdd(mine);
            }));

            parser.AddErrorListener(new ErrorListener());
            NTRIPLESParser.TripleContext cc = null;

            for (;; cc = parser.triple())
            {
                if (cc?.exception != null
                    //&& cc.exception.GetType() != typeof(Antlr4.Runtime.InputMismatchException)
                    //&& cc.exception.GetType() != typeof(Antlr4.Runtime.NoViableAltException)
                )
                {
                    Console.WriteLine(cc.exception.Message);
                    Console.WriteLine(
                        $"found {cc.exception.OffendingToken.Text} at Line {cc.exception.OffendingToken.Line} offset at {cc.exception.OffendingToken.StartIndex}");
                }
                
                if (parser.CurrentToken.Type == TokenConstants.Eof)
                {
                    break;
                }
            }
            // deal with the remainder of the batch.
            var mine2 = new List<Node>();
            while (!batch.IsEmpty)
            {
                if (batch.TryDequeue(out var nnnnnn))
                {
                    mine2.Add(nnnnnn);    
                }
            }
            if (mine2.Count > 0)
            {
                await GroupAndAdd(mine2);
            }
        }

        public override async Task Load(LoadFile request, IServerStreamWriter<LoadFileResponse> responseStream, ServerCallContext context)
        {
            if (request.Type == "nt")
            {
                Console.WriteLine($"Loading file: {request.Path}");
                try
                {
                    Stream data = null;
                    var TotalLength = 0L;
                    var TotalProgress = 0L;
                    var totalLengthDetermined = false;
                    if (!File.Exists(request.Path))
                    {
                        // TODO; download it.
                        var client = new HttpClient();
                        // var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, request.Path));
                        // if (head.IsSuccessStatusCode)
                        // {
                        //     head.Headers.
                        // }
                        data = await client.GetStreamAsync(request.Path);
                        //TotalLength = data.Length;
                    }
                    else
                    {
                        data = File.OpenRead(request.Path);
                        TotalLength = data.Length;
                        totalLengthDetermined = true;
                    }

                    var timer = Stopwatch.StartNew();
                    await using var cleanup = data;
                    await using var bs = new BufferedStream(data, 4096);
                    {
                        var lastNewLinePosition = 0;
                        var memoryWritePos = 0L;
                        var unReadBytes = 0L;
                        var memory = new byte[81920];
                        await using var mainView = new MemoryStream(memory);
                        do
                        {
                            var newBytesAdded = await bs.ReadAsync(memory, (int)memoryWritePos, (int)(memory.Length - memoryWritePos));
                            if (!totalLengthDetermined)
                            {
                                TotalLength += newBytesAdded;
                            }
                            var newBytesEndPos = memoryWritePos + newBytesAdded;
                            if (newBytesAdded == 0)
                                break;
                            // move back to beginning
                            mainView.Seek(0, SeekOrigin.Begin);
                            
                            // find good place to stop
                            //var endOfLastTriple = await ParseNTStreamNoTree(new MemoryStream(memory, 0,(int)newBytesEndPos));
                            var endOfLastTriple = ParseForNewLine(new Span<byte>(memory, 0, (int) newBytesEndPos));
                            // just incase we have have reached the end, we want to make sure we try to read the last bit...
                            if (endOfLastTriple == 0 && newBytesEndPos > 0)
                            {
                                unReadBytes = newBytesEndPos;
                            }
                            else
                            {
                                unReadBytes = newBytesEndPos - endOfLastTriple;
                            }
                            
                            // create a vew up to that position;
                            var pageView = new MemoryStream(memory, 0, endOfLastTriple);

                            // run the parser on that pageView.
                            await ParseNTStream(pageView);
                            // move the end of the stream that we didn't read back to the beginning
                            //Console.WriteLine($"BBC: endoflastTriple:{endOfLastTriple}, unReadBytes:{unReadBytes}, urb32:{Convert.ToInt32(unReadBytes)}");
                            Buffer.BlockCopy(memory, endOfLastTriple, memory, 0, Convert.ToInt32(unReadBytes));
                            memoryWritePos = unReadBytes;
                            TotalProgress += endOfLastTriple;
                            if (timer.Elapsed > TimeSpan.FromSeconds(1))
                            {
                                
                                await responseStream.WriteAsync(new LoadFileResponse
                                {
                                    Progress = TotalProgress,
                                    Length = TotalLength
                                });    
                            
                            }

                            if (context.CancellationToken.IsCancellationRequested)
                            {
                                Console.WriteLine($"Cancel requested for file load. {request.Path }");
                                break;
                            }
                        } while (unReadBytes > 0);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            } else if (request.Type == "graphml")
            {
                Console.WriteLine($"Loading file: {request.Path}");
                try
                {
                    var sw = Stopwatch.StartNew();
                    var nodes = TinkerPop.buildNodesFromFile(request.Path);
                    await _db.Add(nodes).ContinueWith(adding =>
                    {
                        if (adding.IsCompletedSuccessfully)
                        {
                            sw.Stop();
                            Console.WriteLine(
                                $"\nstatus> put done in {sw.ElapsedMilliseconds}ms");
                        }
                        else
                        {
                            Console.WriteLine(
                                $"\nstatus> put err({adding?.Exception?.InnerException?.Message})");
                        }
                    });
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }

        public override async Task Get(Query request, IServerStreamWriter<Node> responseStream, ServerCallContext context)
        {
            try
            {
                // todo: pass this into the Items call. context.CancellationToken;
                var resutl = await _db.Items(request.Iris.Select(iri => new NodeID
                {
                    Iri = iri,
                    Remote = "",
                    Pointer = Utils.NullMemoryPointer()
                }), request.Step);
                var sendCount = 0;
                foreach (var chunk in resutl)
                {
                    var (z, b) = chunk;
                    if (b.IsLeft)
                    {
                        sendCount++;
                        await responseStream.WriteAsync(b.Left);
                    }
                    else
                    {
                        _logger.LogError(b.Right, "Failure processing query");
                    }
                }
                _logger.LogInformation($"Sent back {sendCount} items.");
            }
            catch (Exception e)
            {
                _logger.LogError(e,"Get failed");
            }
        }

        public override async Task<PutResponse> Put(Node request, ServerCallContext context)
        {
            try
            {
                await _db.Add(new[] {request});
                return new PutResponse{Success = true};
            }
            catch (Exception e)
            {
                _logger.LogError(e,"Put failed");
                return new PutResponse{Success = false};
            }
        }

        public override async Task<GetStatsResponse> GetStats(GetStatsRequest request, ServerCallContext context)
        {

            return await _db.GetStats(request, context.CancellationToken);
            
        }

        public override Task ListPolicies(ListPoliciesRequest request, IServerStreamWriter<Node> responseStream, ServerCallContext context)
        {
            return base.ListPolicies(request, responseStream, context);
        }

        public override Task<ListStatsResponse> ListStats(ListStatsRequest request, ServerCallContext context)
        {
            return base.ListStats(request, context);
        }
    }
}
