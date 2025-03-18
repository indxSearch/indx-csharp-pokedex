using Indx.Api;
using Indx.Json;
using Indx.Json.Api;
using Indx.JsonHelper;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace IndxConsoleApp
{
    internal class Program
    {
        private static void Main()
        {
            // 
            // INITIALIZATION & DATA LOAD
            // 

            // Create search engine instance
            var SearchEngine = new SearchEngineJson();

            // Display header
            AnsiConsole.Write(
                new FigletText("indx v4.0.0")
                    .Centered()
                    .Color(Color.White));

            // Prompt for dataset selection
            var fileName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Choose a [blue]dataset[/]?")
                    .HighlightStyle(new Style(Color.Black, Color.White))
                    .PageSize(10)
                    .MoreChoicesText("[grey](Move up and down to reveal more choices)[/]")
                    .AddChoices(
                        "pokedex", "boligmappa", "millum"
                    ));

            // Locate file (adjust relative path if needed)
            string file = "data/" + fileName + ".json";
            if (!File.Exists(file))
                file = "../../../" + file;

            // Set encoding for console
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            // Stream data from file and initialize SearchEngine
            AnsiConsole.Markup($"\rProcessing {file}");
            using (FileStream fstream = File.Open(file, FileMode.Open, FileAccess.Read))
            {
                SearchEngine.Init(fstream, out string _errorMessage);
            }
            if (SearchEngine.DocumentFields == null)
                return;
            PrintFields(false, SearchEngine.DocumentFields);

            // 
            // CONFIGURE FIELDS
            // 

            Field sortField = null!;
            if (fileName == "pokedex")
            {
                SearchEngine.GetField("pokedex_number")!.Indexable = true;
                SearchEngine.GetField("pokedex_number")!.Weight = Weight.High;
                SearchEngine.GetField("pokedex_number")!.Filterable = true;

                SearchEngine.GetField("name")!.Indexable = true;
                SearchEngine.GetField("name")!.Weight = Weight.High;

                SearchEngine.GetField("type1")!.Indexable = true;
                SearchEngine.GetField("type1")!.Weight = Weight.Low;
                SearchEngine.GetField("type1")!.Facetable = true;

                SearchEngine.GetField("type2")!.Indexable = true;
                SearchEngine.GetField("type2")!.Weight = Weight.Low;
                SearchEngine.GetField("type2")!.Facetable = true;

                SearchEngine.GetField("is_legendary")!.Facetable = true;
                SearchEngine.GetField("is_legendary")!.Filterable = true;

                SearchEngine.GetField("weight_kg")!.Facetable = true;

                SearchEngine.GetField("attack")!.Facetable = true;
                SearchEngine.GetField("attack")!.Sortable = true;

                SearchEngine.GetField("hp")!.Facetable = true;

                SearchEngine.GetField("abilities")!.Facetable = true;

                sortField = SearchEngine.GetField("attack")!;
            }
            else if (fileName == "millum")
            {
                SearchEngine.GetField("Navn")!.Indexable = true;
            }

            // 
            // LOAD DATA FROM JSON
            // 

            using (FileStream fstream = File.Open(file, FileMode.Open, FileAccess.Read))
            {
                DateTime loadStart = DateTime.Now;
                AnsiConsole.Status()
                    .Start($"Loading {file}", ctx =>
                    {
                        SearchEngine.LoadJson(fstream, out _);
                    });
                double loadTime = (DateTime.Now - loadStart).TotalMilliseconds;
                Console.WriteLine($"\rLoading {file} completed in {((int)loadTime / 1000.0):F1} seconds\n");
            }

            // 
            // INDEXING
            // 

            if (!SearchEngine.Index())
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: No fields marked as indexable");
                return;
            }
            else
            {
                SearchEngine.Index();
            }

            DateTime indexStart = DateTime.Now;
            double indexTime = 0;
            AnsiConsole.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn().CompletedStyle(Color.Grey85).RemainingStyle(Color.Grey15),
                    new PercentageColumn()
                )
                .Start(ctx =>
                {
                    var task = ctx.AddTask("Indexing", autoStart: false);
                    task.StartTask();
                    while (SearchEngine.Status.SystemState != SystemState.Ready)
                    {
                        task.Value = SearchEngine.Status.IndexProgressPercent;
                        Thread.Sleep(50);
                    }
                    task.Value = 100;
                    task.Description = "[bold green]Complete[/]";
                });

            indexTime = (DateTime.Now - indexStart).TotalMilliseconds;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"🟢 Indexed '{file}' ({SearchEngine.Status.DocumentCount} documents) and ready to search in {(indexTime / 1000.0):F1} seconds\n");
            Console.ResetColor();

            // 
            // SET UP FILTERS & BOOST
            // 
            Filter combinedFilters = null!;
            int docsBoosted = 0;

            if (fileName == "pokedex")
            {
                // FILTER
                Filter origFilter = SearchEngine.CreateRangeFilter("pokedex_number", 1, 151)!;
                combinedFilters = origFilter; // could combine additional filters here with & operator

                // BOOST
                Filter legendaryFilter = SearchEngine.CreateValueFilter("is_legendary", true)!;
                var legendaryBoost = new Boost[1];
                legendaryBoost[0] = new Boost(legendaryFilter, BoostStrength.Med);
                docsBoosted = SearchEngine.DefineBoost(legendaryBoost);
            }

            // 
            // WAIT FOR USER TO START SEARCHING
            // 
            Console.WriteLine("Press [SPACE] to start searching...");
            while (Console.ReadKey(intercept: true).Key != ConsoleKey.Spacebar) { }
            Console.Clear();

            // 
            // INTERACTIVE SEARCH (Live Display)
            // 

            // Set up initial query and state variables
            string text = string.Empty;
            int num = 5;
            var query = new JsonQuery(text, num);
            bool enableFilters = false;
            bool enableBoost = false;
            bool allowEmptySearch = false;
            bool printFacets = false;
            bool truncateList = true;
            bool sortList = false;
            bool measurePerformance = false;
            bool performanceMeasured = false;
            int truncationIndex = 0;
            double latency = 0.0;
            long memoryUsed = 0;
            bool continuousMeasure = true;
            DateTime lastInputTime = DateTime.Now;

            AnsiConsole.Live(new Rows([]))
                .Start(ctx =>
                {
                    while (true)
                    {
                        // Process key input if available (non-blocking)
                        while (Console.KeyAvailable)
                        {
                            var keyInfo = Console.ReadKey(intercept: true);

                            // Always process ESC immediately.
                            if (keyInfo.Key == ConsoleKey.Escape)
                                return; // exit live loop

                            // If idle time is less than 2 seconds, process normal input.
                            if ((DateTime.Now - lastInputTime).TotalSeconds < 2)
                            {
                                if (keyInfo.Key == ConsoleKey.Backspace && text.Length > 0)
                                    text = text.Substring(0, text.Length - 1);
                                else if (keyInfo.Key == ConsoleKey.UpArrow)
                                    num++;
                                else if (keyInfo.Key == ConsoleKey.DownArrow)
                                    num = Math.Max(1, num - 1);
                                else if (!char.IsControl(keyInfo.KeyChar))
                                    text += keyInfo.KeyChar;
                            }
                            else
                            {
                                // Idle for 2+ seconds: allow toggle keys.
                                switch (keyInfo.Key)
                                {
                                    case ConsoleKey.C:
                                        text = "";
                                        lastInputTime = DateTime.Now;
                                        continue;
                                    case ConsoleKey.T:
                                        truncateList = !truncateList;
                                        continue;
                                    case ConsoleKey.F:
                                        if (combinedFilters != null)
                                            enableFilters = !enableFilters;
                                        continue;
                                    case ConsoleKey.P:
                                        printFacets = !printFacets;
                                        break;
                                    case ConsoleKey.B:
                                        if (docsBoosted > 0)
                                            enableBoost = !enableBoost;
                                        continue;
                                    case ConsoleKey.E:
                                        allowEmptySearch = !allowEmptySearch;
                                        continue;
                                    case ConsoleKey.M:
                                        measurePerformance = !measurePerformance;
                                        continue;
                                    case ConsoleKey.S:
                                        if (sortField != null)
                                            sortList = !sortList;
                                        continue;
                                    default:
                                        // For non-toggle keys, process as normal input.
                                        if (keyInfo.Key == ConsoleKey.Backspace && text.Length > 0)
                                            text = text.Substring(0, text.Length - 1);
                                        else if (keyInfo.Key == ConsoleKey.UpArrow)
                                            num++;
                                        else if (keyInfo.Key == ConsoleKey.DownArrow)
                                            num = Math.Max(1, num - 1);
                                        else if (!char.IsControl(keyInfo.KeyChar))
                                            text += keyInfo.KeyChar;
                                        break;
                                }
                            }
                            // Update last input time after processing any key.
                            lastInputTime = DateTime.Now;
                        } // end inner while

                        // Update query parameters
                        query.Text = text;
                        query.MaxNumberOfRecordsToReturn = num;
                        if (sortField != null)
                            query.SortBy = sortList ? sortField : null;
                        query.Filter = enableFilters ? combinedFilters! : null!;
                        if (docsBoosted > 0)
                            query.EnableBoost = enableBoost;

                        // Build search results table
                        var table = new Table().Border(TableBorder.Horizontal);

                        if(fileName == "pokedex")
                        {
                            table.Expand();
                            table.AddColumn("Name");
                            table.AddColumn("Pokedex #");
                            table.AddColumn("Types");
                            table.AddColumn("Stats");
                            table.AddColumn("Score");
                        }

                        var jsonResult = SearchEngine.Search(query);
                        int minimumScore = 0;
                        truncationIndex = jsonResult.TruncationIndex;
                        if (jsonResult != null)
                        {
                            for (int i = 0; i < jsonResult.Records.Length; i++)
                            {
                                var key = jsonResult.Records[i].DocumentKey;
                                var score = jsonResult.Records[i].Score;
                                string json = SearchEngine.GetJsonDataOfKey(key);
                                if (score < minimumScore)
                                    break;

                                if(fileName == "pokedex")
                                {
                                    var pokenum = JsonHelper.GetFieldValue(json, "pokedex_number");
                                    var name = JsonHelper.GetFieldValue(json, "name");
                                    var type1 = JsonHelper.GetFieldValue(json, "type1");
                                    var type2 = JsonHelper.GetFieldValue(json, "type2");
                                    var weight = JsonHelper.GetFieldValue(json, "weight_kg");
                                    var attack = JsonHelper.GetFieldValue(json, "attack");
                                    var health = JsonHelper.GetFieldValue(json, "hp");
                                    var legendary = JsonHelper.GetFieldValue(json, "is_legendary");
                                    var legendarySymbol = legendary == "True" ? "🌟" : "";

                                    table.AddRow(
                                        new Panel(new Markup($"{name} {legendarySymbol}"))
                                            .Border(BoxBorder.None)
                                            .PadLeft(0)
                                            .Padding(new Padding(1)),
                                        new Panel(new Markup(pokenum))
                                            .Border(BoxBorder.None)
                                            .PadLeft(0)
                                            .Padding(new Padding(1)),
                                        new Panel(new Markup($"{type1}, {type2}"))
                                            .Border(BoxBorder.None)
                                            .PadLeft(0)
                                            .Padding(new Padding(1)),
                                        new Panel(new Markup($"A: {attack} / HP: {health} / W: {weight}"))
                                            .Padding(new Padding(0))
                                            .PadLeft(1)
                                            .PadRight(1)
                                            .Expand(),
                                        new Panel(new Markup($"{score}"))
                                            .Border(BoxBorder.None)
                                            .PadLeft(0)
                                            .Padding(new Padding(1))
                                    );

                                }
                            }
                        }

                        // Prepare header markup; escape dynamic text to avoid markup parsing errors.
                        var inputField = new Markup("🔍 Search: " + Markup.Escape(text) + "\n");

                        // Render list
                        var renderables = new List<IRenderable>
                        {
                            inputField,
                            table
                        };

                        // If idle for 2+ seconds and there is text (or empty search is allowed),
                        // add facets, performance info, and prompt instructions.
                        if ((DateTime.Now - lastInputTime).TotalSeconds >= 2 && (text.Length > 0 || allowEmptySearch))
                        {
                            query.EnableFacets = true;
                            var facetResult = SearchEngine.Search(query);
                            Dictionary<string, KeyValuePair<string, int>[]>? facets = facetResult.Facets;
                            Markup facetsMarkup = new Markup("");
                            if (printFacets && facets != null)
                            {
                                var sb = new StringBuilder();
                                foreach (var field in SearchEngine.DocumentFields.GetFacetableFieldList())
                                {
                                    var fName = field.Name;
                                    // Escape field name so that any accidental markup is ignored.
                                    sb.AppendLine($"[bold]{Markup.Escape(fName)}[/]");
                                    if (facets.TryGetValue(fName, out var histogram) && histogram != null)
                                    {
                                        foreach (var item in histogram)
                                            sb.AppendLine($"{Markup.Escape(item.Key)} ({item.Value})");
                                        sb.AppendLine("");
                                    }
                                }
                                facetsMarkup = new Markup(sb.ToString());
                            }
                            if (!allowEmptySearch)
                                query.EnableFacets = false;

                            // Additional info: hit count and, if enabled, performance measurements.
                            Markup additionalInfo = new Markup($"\nExact hits: {truncationIndex + 1}\n");
                            Markup performanceMeta = new Markup("");
                            if (measurePerformance)
                            {
                                int numReps = 100;
                                if(!performanceMeasured || continuousMeasure)
                                {
                                    DateTime perfStart = DateTime.Now;
                                    Parallel.For(1, numReps, i => { SearchEngine.Search(query); });
                                    latency = (DateTime.Now - perfStart).TotalMilliseconds / numReps;
                                    memoryUsed = GC.GetTotalMemory(false) / 1024 / 1024;
                                }
                                performanceMeta = new Markup(
                                    $"Response time {latency:F3} ms (avg of {numReps} reps) filters ({enableFilters}) facets ({query.EnableFacets})\n" +
                                    $"Memory used: {memoryUsed} MB\n" +
                                    $"Document count: {SearchEngine.Status.DocumentCount}\n" +
                                    $"Docs boosted: {(enableBoost ? docsBoosted : 0)}\n" +
                                    $"Version: {SearchEngine.Status.Version}\n" +
                                    $"Valid License: {SearchEngine.Status.ValidLicense} / Expires {SearchEngine.Status.LicenseExpirationDate.ToShortDateString()}");
                                performanceMeasured = true;
                            } else performanceMeasured = false;

                            // Prompt text: note the square brackets for keys are escaped.
                            var promptText = new Markup(
                                "[darkblue]Press [[ESC]] to quit, [[C]] to clear, or type to continue searching.[/]\n" +
                                "[cyan][[T]] Truncation " + (truncateList ? "(On)" : "(Off)") + "[/], " +
                                "[cyan][[F]] Filters " + (enableFilters ? "(On)" : "(Off)") + "[/], " +
                                "[cyan][[P]] Print facets " + (printFacets ? "(On)" : "(Off)") + "[/], " +
                                "[cyan][[B]] Boost " + (enableBoost ? "(On)" : "(Off)") + "[/]\n" +
                                "[cyan][[E]] Empty search " + (allowEmptySearch ? "(On)" : "(Off)") + "[/], " +
                                "[cyan][[M]] Measure performance " + (measurePerformance ? "([green]On[/])" : "(Off)") + "[/], " +
                                "[cyan][[S]] Sorting " + (sortList ? "(On)" : "(Off)") + "[/]"
                            );
                            renderables.Add(facetsMarkup);
                            renderables.Add(additionalInfo);
                            if(measurePerformance) renderables.Add(performanceMeta);
                            renderables.Add(promptText);
                        }

                        // Combine renderables in a vertical stack.
                        var renderStack = new Rows(renderables);

                        // Update the live display (without an outer border).
                        ctx.UpdateTarget(renderStack);

                        Thread.Sleep(50);
                    }
                });
        } // end Main

        /// Prints detected JSON fields.
        public static void PrintFields(bool printToDebugWindow, DocumentFields documentFields)
        {
            var fields = documentFields.GetFieldList();
            fields.Sort((x, y) => x.Name.CompareTo(y.Name));
            
            if (printToDebugWindow)
            {
                foreach (var field in fields)
                {
                    var printLine = $"{field.Name} ({field.Type}) \t {(field.IsArray ? "IsArray" : "")} \t{(field.Optional ? "Optional" : "")}";
                    System.Diagnostics.Debug.WriteLine(printLine);
                }
                return;
            }
            
            var table = new Spectre.Console.Table();
            table.Border = TableBorder.Rounded;
            table.Title = new TableTitle("\n[bold blue]Detected JSON Fields[/]");
            
            // Add columns.
            table.AddColumn(new TableColumn("[bold]Field Name[/]").LeftAligned());
            table.AddColumn(new TableColumn("[bold]Type[/]").Centered());
            table.AddColumn(new TableColumn("[bold]Is Array?[/]").Centered());
            table.AddColumn(new TableColumn("[bold]Optional?[/]").Centered());
            
            // Add rows using Markup cells.
            foreach (var field in fields)
            {
                table.AddRow(
                    new Markup(field.Name),
                    new Markup(field.Type.ToString()),
                    new Markup(field.IsArray ? "Yes" : "No"),
                    new Markup(field.Optional ? "Yes" : "No")
                );
            }
            
            // Render the table.
            AnsiConsole.Write(table);
        }

    } // end class Program
} // end namespace