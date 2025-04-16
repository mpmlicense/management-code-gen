using Microsoft.Data.SqlClient;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public partial class Program
{
    static async Task Main(string[] args)
    {
        string? connStr = args.FirstOrDefault(a => a.StartsWith("--conn="))?.Split("=", 2)[1];
        string? table = args.FirstOrDefault(a => a.StartsWith("--table="))?.Split("=", 2)[1];
        string? projectName = args.FirstOrDefault(a => a.StartsWith("--project="))?.Split("=", 2)[1];
        string? outputDir = args.FirstOrDefault(a => a.StartsWith("--output="))?.Split("=", 2)[1];

        if (string.IsNullOrWhiteSpace(connStr) || string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(projectName))
        {
            Console.WriteLine("Missing connection string, table name, or project name.");
            return;
        }

        // Default output directory if not provided
        outputDir ??= Directory.GetCurrentDirectory();
        string projectDir = Path.Combine(outputDir, projectName);

        if (Directory.Exists(projectDir))
        {
            Console.WriteLine("Project already exists. Please choose a different name.");
            return;
        }

        // Create the API project
        Console.WriteLine($"Creating API project {projectName} in {outputDir}...");
        CreateApiProject(projectName, outputDir);

        AddDefaultConnectionToAppSettings(projectDir, connStr);

        var pkColumn = await GetPrimaryKeyColumn(connStr, table);

        // Generate Model, Controller, and DbContext
        await GenerateModelAndController(connStr, table, projectDir, projectName, pkColumn);
        await GenerateDbContext(connStr, table, projectDir, projectName);
        await GenerateProgram(connStr, table, projectDir, projectName);

        // Add EF Core NuGet packages
        AddEfCoreNuGetPackages(projectDir);

        Console.WriteLine("Project generated successfully!");
    }

    static async Task<string> GetPrimaryKeyColumn(string connStr, string table)
    {
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        var cmd = new SqlCommand(@"
            SELECT KU.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS TC
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS KU
              ON TC.CONSTRAINT_NAME = KU.CONSTRAINT_NAME
            WHERE TC.TABLE_NAME = @table AND TC.CONSTRAINT_TYPE = 'PRIMARY KEY'", conn);
        cmd.Parameters.AddWithValue("@table", table);
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? "Id";
    }

    static void CreateApiProject(string projectName, string outputDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"new webapi -n {projectName} --use-controllers")
        {
            WorkingDirectory = outputDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        System.Diagnostics.Process.Start(psi)?.WaitForExit();
    }

    static void AddDefaultConnectionToAppSettings(string projectDir, string connStr)
    {
        string appSettingsPath = Path.Combine(projectDir, "appsettings.json");

        // Create or update appsettings.json
        var appSettings = new
        {
            Logging = new
            {
                LogLevel = new Dictionary<string, string>
                {
                    { "Default", "Information" },
                    { "Microsoft.AspNetCore", "Warning" } // Using the valid name with a dot
                }
            },
            AllowedHosts = "*",
            ConnectionStrings = new
            {
                DefaultConnection = connStr
            }
        };
        string jsonContent = System.Text.Json.JsonSerializer.Serialize(appSettings, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(appSettingsPath, jsonContent);
        Console.WriteLine($"Updated appsettings.json with DefaultConnection: {connStr}");
    }

    static async Task GenerateModelAndController(string connStr, string table, string projectDir, string projectName, string pkColumn)
    {
        var model = new StringBuilder();
        model.AppendLine($"using System.ComponentModel.DataAnnotations.Schema;");
        model.AppendLine($"using System.ComponentModel.DataAnnotations;");

        model.AppendLine($"namespace {projectName}.Models;");

        model.AppendLine($"[Table(\"{table}\")]");

        var modelClassName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(table);
        model.AppendLine($"public class {modelClassName}");
        model.AppendLine("{");

        string pkColumnType = "string"; // Default to string if not found

        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        var cmd = new SqlCommand(@"SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table", conn);
        cmd.Parameters.AddWithValue("@table", table);
        var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string col = reader.GetString(0);
            string type = reader.GetString(1);
            bool nullable = reader.GetString(2) == "YES";
            string csType = type switch
            {
                "int" => "int",
                "bigint" => "long",
                "nvarchar" or "varchar" or "text" => "string",
                "bit" => "bool",
                "datetime" or "datetime2" => "DateTime",
                "uniqueidentifier" => "Guid",
                _ => "string"
            };
            if (nullable && csType != "string") csType += "?";

            if (col.Equals(pkColumn))
            {
                pkColumnType = csType;
                model.AppendLine($"    [Key]");
            }
            model.AppendLine($"    [Column(\"{col}\")]");
            model.AppendLine($"    public {csType} {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(col)} {{ get; set; }}");
        }
        model.AppendLine("}");
        string modelPath = Path.Combine(projectDir, "Models", $"{table}.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllTextAsync(modelPath, model.ToString());
        Console.WriteLine($"Model generated: {modelPath}");

        // Generate Controller
        var controller = new StringBuilder();
        string controllerName = projectName + "Controller";
        controller.AppendLine("using Microsoft.AspNetCore.Mvc;");
        controller.AppendLine("using Microsoft.EntityFrameworkCore;");
        controller.AppendLine("using System.Linq.Dynamic.Core;");
        controller.AppendLine($"using {projectName}.Data;");
        controller.AppendLine($"using {projectName}.Models;");
        controller.AppendLine($"namespace {projectName}.Controllers;");
        controller.AppendLine("[ApiController]");
        controller.AppendLine("[Route(\"api/[controller]\")]");
        controller.AppendLine($"public class {controllerName} : ControllerBase");
        controller.AppendLine("{");
        controller.AppendLine("    private readonly AppDbContext _context;");
        controller.AppendLine($"    public {controllerName}(AppDbContext context) => _context = context;");
        controller.AppendLine($"    [HttpGet]");
        controller.AppendLine($"    public async Task<ActionResult<IEnumerable<{modelClassName}>>> GetAll(");
        controller.AppendLine($"        [FromQuery] int page = 1,");
        controller.AppendLine($"        [FromQuery] int pageSize = 1000,");
        controller.AppendLine($"        [FromQuery] string? search = null,");
        controller.AppendLine($"        [FromQuery] string? searchColumn = null,");
        controller.AppendLine($"        [FromQuery] string? sortBy = null)");
        controller.AppendLine($"    {{");
        controller.AppendLine($"        var query = _context.{table}.AsQueryable();");
        controller.AppendLine();
        controller.AppendLine($"        // Apply search filter");
        controller.AppendLine($"        if (!string.IsNullOrEmpty(search) && !string.IsNullOrEmpty(searchColumn))");
        controller.AppendLine($"        {{");
        controller.AppendLine($"            var property = typeof({modelClassName}).GetProperty(searchColumn, System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);");
        controller.AppendLine($"            if (property != null)");
        controller.AppendLine($"            {{");
        controller.AppendLine($"                query = query.Where(e => property.GetValue(e) != null && EF.Functions.Like(property.GetValue(e).ToString(), $\"%{{search}}%\"));");
        controller.AppendLine($"            }}");
        controller.AppendLine($"        }}");
        controller.AppendLine();
        controller.AppendLine($"        // Apply sorting");
        controller.AppendLine($"        if (!string.IsNullOrEmpty(sortBy))");
        controller.AppendLine($"        {{");
        controller.AppendLine($"            query = query.OrderBy(sortBy); // Requires dynamic sorting logic");
        controller.AppendLine($"        }}");
        controller.AppendLine();
        controller.AppendLine($"        // Apply pagination");
        controller.AppendLine($"        var totalItems = await query.CountAsync();");
        controller.AppendLine($"        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();");
        controller.AppendLine();
        controller.AppendLine($"        // Return paginated result");
        controller.AppendLine($"        return Ok(new");
        controller.AppendLine($"        {{");
        controller.AppendLine($"            TotalItems = totalItems,");
        controller.AppendLine($"            Page = page,");
        controller.AppendLine($"            PageSize = pageSize,");
        controller.AppendLine($"            Items = items");
        controller.AppendLine($"        }});");
        controller.AppendLine($"    }}");
        controller.AppendLine($"    [HttpGet(\"{{id}}\")]");
        controller.AppendLine($"    public async Task<ActionResult<{modelClassName}>> GetById({pkColumnType} id) => await _context.{table}.FindAsync(id) is {modelClassName} item ? Ok(item) : NotFound();");
        controller.AppendLine($"    [HttpPost]");
        controller.AppendLine($"    public async Task<ActionResult<{modelClassName}>> Create({modelClassName} input) {{ _context.{table}.Add(input); await _context.SaveChangesAsync(); return CreatedAtAction(nameof(GetById), new {{ id = input.{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(pkColumn)} }}, input); }}");
        controller.AppendLine($"    [HttpPut(\"{{id}}\")]");
        controller.AppendLine($"    public async Task<IActionResult> Update({pkColumnType} id, {modelClassName} input) {{ if (!id.Equals(input.{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(pkColumn)})) return BadRequest(); _context.Entry(input).State = EntityState.Modified; await _context.SaveChangesAsync(); return NoContent(); }}");
        controller.AppendLine($"    [HttpDelete(\"{{id}}\")]");
        controller.AppendLine($"    public async Task<IActionResult> Delete({pkColumnType} id) {{ var item = await _context.{table}.FindAsync(id); if (item == null) return NotFound(); _context.{table}.Remove(item); await _context.SaveChangesAsync(); return NoContent(); }}");
        controller.AppendLine("}");

        string controllerPath = Path.Combine(projectDir, "Controllers", $"{controllerName}.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(controllerPath)!);
        await File.WriteAllTextAsync(controllerPath, controller.ToString());
        Console.WriteLine($"Controller generated: {controllerPath}");

        string weatherControllerPath = Path.Combine(projectDir, "Controllers", "WeatherForecastController.cs");
        if (File.Exists(weatherControllerPath))
        {
            File.Delete(weatherControllerPath);
            Console.WriteLine($"Deleted WeatherForecastController.cs");
        }
    }

    static async Task GenerateDbContext(string connStr, string table, string projectDir, string projectName)
    {
        var modelClassName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(table);


        var dbContext = new StringBuilder();
        dbContext.AppendLine("using Microsoft.EntityFrameworkCore;");
        dbContext.AppendLine($"using {projectName}.Models;");
        dbContext.AppendLine($"namespace {projectName}.Data;");
        dbContext.AppendLine("public class AppDbContext : DbContext");
        dbContext.AppendLine("{");
        dbContext.AppendLine("    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }");
        dbContext.AppendLine($"    public DbSet<{modelClassName}> {table} {{ get; set; }}");
        dbContext.AppendLine("}");

        string dbContextPath = Path.Combine(projectDir, "Data", "AppDbContext.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(dbContextPath)!);
        await File.WriteAllTextAsync(dbContextPath, dbContext.ToString());
        Console.WriteLine($"DbContext generated: {dbContextPath}");
    }

    static async Task GenerateProgram(string connStr, string table, string projectDir, string projectName)
    {
        var programFile = new StringBuilder();
        programFile.AppendLine("using Microsoft.EntityFrameworkCore;");
        programFile.AppendLine("using Microsoft.OpenApi.Models;");
        programFile.AppendLine($"using {projectName}.Data;");
        programFile.AppendLine($"var builder = WebApplication.CreateBuilder(args);");
        programFile.AppendLine();
        programFile.AppendLine("// Add services to the container.");
        programFile.AppendLine("builder.Services.AddDbContext<AppDbContext>(options =>");
        programFile.AppendLine("    options.UseSqlServer(builder.Configuration.GetConnectionString(\"DefaultConnection\")));");
        programFile.AppendLine("builder.Services.AddControllers();");
        programFile.AppendLine("builder.Services.AddEndpointsApiExplorer();");
        programFile.AppendLine("builder.Services.AddSwaggerGen();");
        programFile.AppendLine();
        programFile.AppendLine("var app = builder.Build();");
        programFile.AppendLine();
        programFile.AppendLine("// Configure the HTTP request pipeline.");
        programFile.AppendLine("app.UseSwagger();");
        programFile.AppendLine("app.UseSwaggerUI();");
        programFile.AppendLine("app.UseHttpsRedirection();");
        programFile.AppendLine("app.UseAuthorization();");
        programFile.AppendLine("app.MapControllers();");
        programFile.AppendLine("app.Run();");

        string programPath = Path.Combine(projectDir, "Program.cs");
        await File.WriteAllTextAsync(programPath, programFile.ToString());
        Console.WriteLine($"Program generated: {programPath}");
    }

    static void AddEfCoreNuGetPackages(string projectDir)
    {
        var csprojFile = Directory.GetFiles(projectDir, "*.csproj").FirstOrDefault();
        if (csprojFile == null)
        {
            Console.WriteLine("Error: .csproj file not found.");
            return;
        }

        string[] packages = new[] { "Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.SqlServer", "Microsoft.EntityFrameworkCore.Tools", "System.Linq.Dynamic.Core" };

        foreach (var package in packages)
        {
            var processInfo = new System.Diagnostics.ProcessStartInfo("dotnet", $"add \"{csprojFile}\" package {package}")
            {
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new System.Diagnostics.Process();
            process.StartInfo = processInfo;

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, args) => { if (args.Data != null) outputBuilder.AppendLine(args.Data); };
            process.ErrorDataReceived += (sender, args) => { if (args.Data != null) errorBuilder.AppendLine(args.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                Console.WriteLine($"✔ Added {package}");
            }
            else
            {
                Console.WriteLine($"❌ Failed to add {package}: {errorBuilder}");
            }
        }
    }
}
