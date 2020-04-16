using Antlr4.Runtime;
using System.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Ahghee;
using Ahghee.Grpc;
using Antlr4.Runtime.Tree;
using cli_grammer;
using Google.Protobuf;


namespace cli.antlr
{
    public class Listener : AHGHEEBaseListener
    {
        private readonly IStorage _store;
        private bool flushed = false;
        public Listener(IStorage store)
        {
            _store = store;
        }

        public override void ExitPut(AHGHEEParser.PutContext context){
            var pm = GetPrintMode(context.flags());  
            foreach(var nc in context.json()){
                
                var n = JsonParser.Default.Parse<Node>( nc.GetText() );
                n.Id.Pointer = Utils.NullMemoryPointer();
                n.Fragments.Add(Utils.NullMemoryPointer());
                n.Fragments.Add(Utils.NullMemoryPointer());
                n.Fragments.Add(Utils.NullMemoryPointer());
                // n.Attributes.Add();
                if ((pm & PrintMode.Verbose) != 0)
                {
                    Console.WriteLine($"\nstatus> put({n.Id.Iri})");
                }
                var sw = Stopwatch.StartNew();
                var adding = _store.Add(new [] {n}).ContinueWith(adding =>
                {
                    if (adding.IsCompletedSuccessfully)
                    {
                        sw.Stop();
                        Console.WriteLine($"\nstatus> put({n.Id.Iri}).done in {sw.ElapsedMilliseconds}ms");
                    }
                    else
                    {
                        Console.WriteLine($"\nstatus> put({n.Id.Iri}).err({adding?.Exception?.InnerException?.Message})");
                    }
                    Console.Write("\nwat> ");
                });
                flushed = false;
            }
        }

        internal StringBuilder NodeIdPrinter(StringBuilder sb, NodeID nid, int tabs)
        {
            sb.AppendLine();
            if (tabs > 0)
            {
                sb.Append(String.Empty.PadLeft(tabs, '\t'));
            }
            sb.Append("id: ");
            sb.Append(nid.Iri);
            sb.AppendLine();
                                        
            if (!string.IsNullOrEmpty(nid.Remote))
            {
                if (tabs > 0)
                {
                    sb.Append(String.Empty.PadLeft(tabs, '\t'));
                }
                sb.Append("\n  graph: ");
                sb.Append(nid.Remote);
                sb.AppendLine();
            }

            return sb;
        }
        
        internal StringBuilder TypeBytesPrinter(StringBuilder sb,  TypeBytes tb, int tabs)
        {
            sb.AppendLine();
            if (tabs > 0)
            {
                sb.Append(String.Empty.PadLeft(tabs, '\t'));
            }
            sb.Append("type: ");
            sb.Append(tb.Typeiri);
            sb.AppendLine();

            sb.AppendLine();
            if (tabs > 0)
            {
                sb.Append(String.Empty.PadLeft(tabs, '\t'));
            }
            sb.Append("bytes: ");
            sb.Append(tb.Bytes.ToBase64());
            sb.AppendLine();


            return sb;
        }
        
        internal StringBuilder MemoryPointerPrinter(StringBuilder sb,  MemoryPointer mp, int tabs)
        {
            sb.AppendLine();
            var pad = String.Empty.PadLeft(tabs, '\t');

            sb.Append(pad);

            sb.Append("partition: ");
            sb.Append(mp.Partitionkey);
            sb.AppendLine();

            sb.AppendLine();
            sb.Append(pad);
            sb.Append("file: ");
            sb.Append(mp.Filename);
            sb.AppendLine();

            sb.AppendLine();
            sb.Append(pad);
            sb.Append("offset: ");
            sb.Append(mp.Offset);
            sb.AppendLine();
            
            sb.AppendLine();
            sb.Append(pad);
            sb.Append("length: ");
            sb.Append(mp.Length);
            sb.AppendLine();
            
            return sb;
        }
        
        internal StringBuilder DataPrinter(StringBuilder sb, DataBlock db, int tabs)
        {
            return db.DataCase switch
            {
                DataBlock.DataOneofCase.B => sb.Append(db.B),
                DataBlock.DataOneofCase.D => sb.Append(db.D),
                DataBlock.DataOneofCase.F => sb.Append(db.F),
                DataBlock.DataOneofCase.I32 => sb.Append(db.I32),
                DataBlock.DataOneofCase.I64 => sb.Append(db.I64),
                DataBlock.DataOneofCase.Ui32 => sb.Append(db.Ui32),
                DataBlock.DataOneofCase.Ui64 => sb.Append(db.Ui64),
                DataBlock.DataOneofCase.Str => sb.Append(db.Str),
                DataBlock.DataOneofCase.Nodeid => NodeIdPrinter(sb, db.Nodeid, tabs+1),
                DataBlock.DataOneofCase.Metabytes => TypeBytesPrinter(sb, db.Metabytes, tabs+1),
                DataBlock.DataOneofCase.Memorypointer => MemoryPointerPrinter(sb,db.Memorypointer,tabs+1)
            };
        }

        internal void NodePrinter(StringBuilder sb, Node n, int tabs, PrintMode pm)
        {
            sb = NodeIdPrinter(sb, n.Id, tabs);

            IEnumerable<KeyValue> kvs = null;
            if (pm != PrintMode.History)
            {
                kvs = n.Attributes
                    .GroupBy(_ => _.Key.Data,
                        (k, v) => v.OrderByDescending(_ => _.Value.Timestamp).First());
            } else
            {
                kvs = n.Attributes.OrderBy(_ => _.Value.Timestamp);
            }
            
            foreach (var attr in kvs)
            {
                if ((pm & (PrintMode.Times | PrintMode.History)) != 0)
                {
                    sb.Append("\t");
                    sb.Append(attr.Value.Timestamp);    
                }
                sb.Append("\t");
                sb = DataPrinter(sb, attr.Key.Data, tabs+2);
                sb.Append("\t: ");
                sb = DataPrinter(sb, attr.Value.Data, tabs+2);
                sb.AppendLine();
            }
            Console.Write(sb.ToString());
        }

        internal PrintMode GetPrintMode(AHGHEEParser.FlagsContext fc)
        {
            var fgs = fc?.GetText() ?? "";
            PrintMode pm = PrintMode.Simple;
 
            if (fgs.Any(f => f == 'h')) pm |= PrintMode.History;
            if (fgs.Any(_=> _ == 't')) pm |= PrintMode.Times;
            if (fgs.Any(_ => _ == 'v')) pm |= PrintMode.Verbose;
            return pm;
        }
        public override void ExitGet(AHGHEEParser.GetContext context)
        {

            void getNodes(IEnumerable<NodeID> ab, PrintMode pm)
            {
                if ((pm & PrintMode.Verbose) != 0)
                {
                    Console.WriteLine($"\nstatus> get({string.Join("\n,", ab.Select(_ => _.Iri))})");
                }

                var sw = Stopwatch.StartNew();
                var t = _store.Items(ab)
                    .ContinueWith(get =>
                    {
                        try
                        {
                            sw.Stop();
                            if (get.IsCompletedSuccessfully)
                            {
                                var sb = new StringBuilder();
                                foreach (var result in get.Result)
                                {
                                    if (result.Item2 is Either<Node, Exception>.Left _n)
                                    {
                                        sb.Append("\nstatus> get(");
                                        sb.Append(result.Item1.Iri);
                                        sb.Append(").done");

                                        NodePrinter(sb,_n.Item,0,pm);
                                        sb.Clear();
                                    }

                                    if (result.Item2 is Either<Node, Exception>.Right _e)
                                    {
                                        Console.WriteLine($"\nstatus> get({result.Item1.Iri}).err({_e.Item.Message})");
                                    }
                                }

                                Console.WriteLine($"status> completed in {sw.ElapsedMilliseconds}ms");
                            }
                            else
                            {
                                Console.WriteLine($"\nstatus> get(...).err({get?.Exception?.InnerException?.Message})");
                            }

                            Console.Write("\nwat> ");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                            throw;
                        }
                    });
            };

            try
            {
                var pm = GetPrintMode(context.flags());       
                
                
                if (!flushed && ((PrintMode.Verbose & pm) != 0))
                {
                    Console.WriteLine($"\nstatus> flushing writes (todo: cmd autoflush false to disable)");
                    _store.Flush();
                    flushed = true;
                }

                
                var ids = context.nodeid().ToList();
                var ab = ids.Select(id =>
                {
                    var json = id.obj();
                    
                    if (json != null)
                    {
                        var text = json.GetText();
                        var ab = Google.Protobuf.JsonParser.Default.Parse<NodeID>(text);
                        ab.Pointer = Utils.NullMemoryPointer();
                        return ab;
                    }
                
                    var dburi = id.id();

                    var ac = new NodeID
                    {
                        Iri = dburi.GetText().Trim('"'),
                        Pointer = Utils.NullMemoryPointer()
                    };
                    if (id.remote() != null)
                    {
                        ac.Remote = id.remote().GetText().Trim('"');
                    }
                    return ac;
                }).ToList();
                getNodes(ab, pm);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    [Flags]
    internal enum PrintMode
    {
        Simple,
        History,
        Times,
        Verbose
    }

    public class CommandVisitor : AHGHEEBaseVisitor<bool>{
        public bool VisitCommand(AHGHEEParser.CommandContext context){
            return true;
        }
    }

    public class ErrorListener : BaseErrorListener
    {
        
    }
    
    public partial class AHGHEEBaseVisitor<Result>
    {
        
    }
}