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
                new FigletText("indx " + new Version(SearchEngine.Status.Version).ToString(2))
                    .Centered());

            // Dataset
            var fileName = "pokedex";
            // Locate file (adjust relative path if needed)
            string file = "data/" + fileName + ".json";
            if (!File.Exists(file))
                file = "../../../" + file;

            // Set encoding for console
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            // Stream data from file and initialize SearchEngine
            using (FileStream fstream = File.Open(file, FileMode.Open, FileAccess.Read))
            {
                AnsiConsole.Status()
                    .SpinnerStyle(Color.LightSlateBlue)
                    .Spinner(Spinner.Known.Line) // choose a spinner style
                    .Start($"Analyzing JSON", ctx =>
                    {
                        // Perform your loading operation.
                        SearchEngine.Init(fstream, out string _errorMessage);
                    });
            }
            if (SearchEngine.DocumentFields == null)
                return;
            PrintFields(false, SearchEngine.DocumentFields);

            // 
            // CONFIGURE FIELDS
            // 

            Field sortField = null!;

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

            SearchEngine.GetField("classfication")!.Indexable = true;
            SearchEngine.GetField("classfication")!.Weight = Weight.Low;
            SearchEngine.GetField("classfication")!.Facetable = true;

            SearchEngine.GetField("is_legendary")!.Facetable = true;
            SearchEngine.GetField("is_legendary")!.Filterable = true;

            SearchEngine.GetField("attack")!.Sortable = true;

            SearchEngine.GetField("abilities")!.Facetable = true;

            sortField = SearchEngine.GetField("attack")!;


            // 
            // LOAD DATA FROM JSON
            // 

            using (FileStream fstream = File.Open(file, FileMode.Open, FileAccess.Read))
            {
                DateTime loadStart = DateTime.Now;
                AnsiConsole.Status()
                    .SpinnerStyle(Color.LightSlateBlue)
                    .Spinner(Spinner.Known.BouncingBar) // choose a spinner style
                    .Start($"Loading {file}", ctx =>
                    {
                        // Perform your loading operation.
                        SearchEngine.LoadJson(fstream, out _);
                    });
                double loadTime = (DateTime.Now - loadStart).TotalMilliseconds;
                AnsiConsole.Markup($"\nLoading {file} completed in {(int)loadTime / 1000.0:F1} seconds\n");
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
                    new ProgressBarColumn()
                        .CompletedStyle(Color.LightSlateBlue)
                        .RemainingStyle(Color.Grey15)
                        .FinishedStyle(Color.LightSlateBlue),
                    new PercentageColumn()
                        .CompletedStyle(Color.Default)
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
                    task.Description = "[bold]Complete[/]";
                });

            indexTime = (DateTime.Now - indexStart).TotalMilliseconds;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"🟢 Indexed '{file}' ({SearchEngine.Status.DocumentCount} documents) and ready to search in {indexTime / 1000.0:F1} seconds\n");
            Console.ResetColor();

            // 
            // SET UP FILTERS & BOOST
            // 

            Filter combinedFilters = null!;
            int docsBoosted = 0;

            // FILTER
            Filter origFilter = SearchEngine.CreateRangeFilter("pokedex_number", 1, 151)!;
            combinedFilters = origFilter; // could combine additional filters here with & operator

            // BOOST
            Filter legendaryFilter = SearchEngine.CreateValueFilter("is_legendary", true)!;
            var legendaryBoost = new Boost[1];
            legendaryBoost[0] = new Boost(legendaryFilter, BoostStrength.Med);
            docsBoosted = SearchEngine.DefineBoost(legendaryBoost);

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
            int currentFacetPage = 0;

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
                                        currentFacetPage = 0;
                                        continue;
                                    case ConsoleKey.T when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        truncateList = !truncateList;
                                        continue;
                                    case ConsoleKey.F when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        if (combinedFilters != null)
                                            enableFilters = !enableFilters;
                                        continue;
                                    case ConsoleKey.P when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        printFacets = !printFacets;
                                        if (!printFacets) currentFacetPage = 0;
                                        continue;
                                    case ConsoleKey.B when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        if (docsBoosted > 0)
                                            enableBoost = !enableBoost;
                                        continue;
                                    case ConsoleKey.E when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        allowEmptySearch = !allowEmptySearch;
                                        continue;
                                    case ConsoleKey.M when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        measurePerformance = !measurePerformance;
                                        continue;
                                    case ConsoleKey.S when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                        if (sortField != null)
                                            sortList = !sortList;
                                        continue;
                                    case ConsoleKey.LeftArrow when printFacets:
                                        currentFacetPage = Math.Max(0, currentFacetPage - 1);
                                        continue;
                                    case ConsoleKey.RightArrow when printFacets:
                                        currentFacetPage++;
                                        continue;
                                    default:
                                        // For non-toggle keys, process as normal input.
                                        if (keyInfo.Key == ConsoleKey.Backspace && text.Length > 0)
                                        {
                                            text = text.Substring(0, text.Length - 1);
                                            currentFacetPage = 0;
                                        }
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
                        var table = new Table();
                        table.Border(TableBorder.Simple);
                        table.BorderColor(Color.Grey15);
                        table.Expand();

                        table.AddColumn("Name");
                        table.AddColumn("Pokedex #");
                        table.AddColumn("Types");
                        table.AddColumn("Classification");
                        table.AddColumn("Stats [Grey30](Attack, Health, Speed)[/]");
                        table.AddColumn("Score");

                        //
                        // SEARCH
                        //

                        var jsonResult = SearchEngine.Search(query);
                        truncationIndex = jsonResult.TruncationIndex;

                        if (jsonResult != null)
                        {
                            for (int i = 0; i < jsonResult.Records.Length; i++)
                            {
                                var key = jsonResult.Records[i].DocumentKey;
                                var score = jsonResult.Records[i].Score;
                                string json = SearchEngine.GetJsonDataOfKey(key);

                                var pokenum = JsonHelper.GetFieldValue(json, "pokedex_number");
                                var name = JsonHelper.GetFieldValue(json, "name");
                                var type1 = JsonHelper.GetFieldValue(json, "type1");
                                var type2 = JsonHelper.GetFieldValue(json, "type2");
                                var classification = JsonHelper.GetFieldValue(json, "classfication");
                                var speed = JsonHelper.GetFieldValue(json, "speed");
                                var attack = JsonHelper.GetFieldValue(json, "attack");
                                var health = JsonHelper.GetFieldValue(json, "hp");
                                var legendary = JsonHelper.GetFieldValue(json, "is_legendary");
                                var legendarySymbol = legendary == "True" ? "🌟" : "";

                                var stats = new Table();
                                stats.Border(TableBorder.Rounded);
                                stats.BorderColor(Color.Grey30);
                                stats.HideHeaders();
                                stats.AddColumn("Attack");
                                stats.AddColumn("Health");
                                stats.AddColumn("Speed");
                                stats.AddRow(attack, health, speed);

                                table.AddRow(
                                    new Panel(new Markup($"{name} {legendarySymbol}"))
                                        .Border(BoxBorder.None)
                                        .Padding(new Padding(1))
                                        .PadLeft(0),
                                    new Panel(new Markup(pokenum))
                                        .Border(BoxBorder.None)
                                        .Padding(new Padding(1))
                                        .PadLeft(0),
                                    new Panel(new Markup($"{type1} {type2}"))
                                        .Border(BoxBorder.None)
                                        .Padding(new Padding(1))
                                        .PadLeft(0),
                                        new Panel(new Markup(classification))
                                        .Border(BoxBorder.None)
                                        .Padding(new Padding(1))
                                        .PadLeft(0),
                                    new Panel(stats)
                                        .Padding(new Padding(0))
                                        .Expand()
                                        .Border(BoxBorder.None),
                                    new Panel(new Markup($"{score}"))
                                        .Border(BoxBorder.None)
                                        .Padding(new Padding(1))
                                        .PadLeft(0)
                                );
                            }
                        }

                        // Prepare header markup; escape dynamic text to avoid markup parsing errors.
                        string cursor = "█";
                        if ((DateTime.Now - lastInputTime).TotalSeconds >= 2) cursor = "";
                        var inputField = new Markup("🔍 Search: " + Markup.Escape(text) + cursor + "\n");

                        // Render list
                        var renderables = new List<IRenderable>
                        {
                            inputField,
                            table
                        };

                        // If idle for 2+ seconds and there is text (or empty search is allowed),
                        // add facets, performance info, and command instructions.
                        if ((DateTime.Now - lastInputTime).TotalSeconds >= 2 && (text.Length > 0 || allowEmptySearch))
                        {
                            query.EnableFacets = true;
                            var facetResult = SearchEngine.Search(query);
                            Dictionary<string, KeyValuePair<string, int>[]>? facets = facetResult.Facets;
                            Markup facetsMarkup = new Markup("");
                            if (printFacets && facets != null)
                            {
                                // Build a compact string for each facet group.
                                var facetGroups = new List<string>();
                                foreach (var field in SearchEngine.DocumentFields.GetFacetableFieldList())
                                {
                                    var fName = field.Name;
                                    var sb = new StringBuilder();
                                    sb.Append($"[bold]{Markup.Escape(fName)}[/]: ");
                                    if (facets.TryGetValue(fName, out var histogram) && histogram != null)
                                    {
                                        // Join key/value pairs with commas.
                                        var items = histogram.Select(item => $"{Markup.Escape(item.Key)} ({item.Value})");
                                        sb.Append(string.Join(", ", items));
                                    }
                                    facetGroups.Add(sb.ToString());
                                }

                                // Pagination: Show groupsPerPage facet groups per page.
                                int groupsPerPage = 2;
                                if(truncationIndex < 10) groupsPerPage = 4;
                                int totalPages = (int)Math.Ceiling((double)facetGroups.Count / groupsPerPage);
                                if (totalPages == 0)
                                    totalPages = 1;
                                // Ensure currentFacetPage is within bounds (this variable is updated when left/right arrow keys are pressed)
                                if (currentFacetPage >= totalPages)
                                    currentFacetPage = totalPages - 1;
                                if (currentFacetPage < 0)
                                    currentFacetPage = 0;

                                int start = currentFacetPage * groupsPerPage;
                                int count = Math.Min(groupsPerPage, facetGroups.Count - start);
                                var pageFacets = facetGroups.Skip(start).Take(count);
                                string facetText = string.Join("\n\n", pageFacets) +
                                                $"\n\n[grey]Page {currentFacetPage + 1} of {totalPages} [[LEFT/RIGHT]] to navigate)[/]";

                                facetsMarkup = new Markup(facetText);
                            }
    

                            if (!allowEmptySearch)
                                query.EnableFacets = false;

                            // Additional info: hit count and, if enabled, performance measurements.
                            Markup additionalInfo = new Markup($"\nExact hits: {truncationIndex + 1}\n");
                            Markup performanceMeta = new Markup("");
                            if(printFacets) query.EnableFacets = true;
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

                            var promptText = new Markup(
                                "[cyan]Press [[UP/DOWN]] to change num, [[ESC]] to quit, [[C]] to clear, or type to continue searching.[/]\n"
                            );
                            var commands = new Table();
                            commands.Border(TableBorder.Rounded);
                            commands.BorderColor(Color.LightSlateBlue);
                            commands.AddColumn("Key");
                            commands.AddColumn("Command");
                            commands.AddColumn("Status");
                            commands.AddRow("[grey]SHIFT-[/]T", "[grey]Truncation[/]", (truncateList ? "[cyan bold]Enabled[/]" : "Disabled"));
                            commands.AddRow("[grey]SHIFT-[/]F", "[grey]Filters[/]", (enableFilters ? "[cyan bold]Enabled[/]" : "Disabled"));
                            commands.AddRow("[grey]SHIFT-[/]P", "[grey]Print facets[/]", (printFacets  ? "[cyan bold]Enabled[/]" : "Disabled"));
                            commands.AddRow("[grey]SHIFT-[/]B", "[grey]Boosting[/]", (enableBoost ? "[cyan bold]Enabled[/]" : "Disabled"));
                            commands.AddRow("[grey]SHIFT-[/]E", "[grey]Empty search[/]", (allowEmptySearch ? "[cyan bold]Enabled[/]" : "Disabled"));
                            commands.AddRow("[grey]SHIFT-[/]M", "[grey]Measure performance[/]", (measurePerformance ? "[cyan bold]Enabled[/]" : "Disabled"));
                            commands.AddRow("[grey]SHIFT-[/]S", "[grey]Sorting[/]", (sortList ? "[cyan bold]Enabled[/]" : "Disabled"));

                            renderables.Add(facetsMarkup);
                            renderables.Add(additionalInfo);
                            if(measurePerformance) renderables.Add(performanceMeta);
                            renderables.Add(promptText);
                            renderables.Add(commands);
                        }

                        // Combine renderables in a vertical stack
                        var renderStack = new Rows(renderables);
                        ctx.UpdateTarget(renderStack);
                    } // end context
                }); // end Live view
        } // end Main

        /// Prints detected JSON fields
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
            
            var table = new Table();
            table.BorderColor(Color.Grey50);
            table.Expand();
            table.Border = TableBorder.Horizontal;
            table.Title = new TableTitle("\n[LightSlateBlue]Detected JSON Fields[/]\n");
            
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
        } // end function PrintFields

    } // end class Program
} // end namespace