using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using App.Metrics;
using App.Metrics.Formatters.Prometheus;
using App.Metrics.Gauge;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreTempPrometheusExporter
{
	public class Program
	{

	
		public static void Main(string[] args)
		{
			CreateHostBuilder(args).Build().Run();
		}

		public static IHostBuilder CreateHostBuilder(string[] args)
		{
					var metrics = AppMetrics.CreateDefaultBuilder()
				
						.Configuration.Configure(options =>
						{
							options.GlobalTags = new GlobalMetricTags();
							
						})
						.OutputMetrics.AsPrometheusPlainText()
						.OutputMetrics.AsPrometheusProtobuf()
						.Build();

				

			var hostBuilder = Host.CreateDefaultBuilder(args)
				.ConfigureMetrics(metrics).UseMetricsEndpoints(endpointsOptions =>
				{
					endpointsOptions.MetricsTextEndpointOutputFormatter = metrics.OutputMetricsFormatters.OfType<MetricsPrometheusTextOutputFormatter>().First();
					endpointsOptions.MetricsEndpointOutputFormatter = metrics.OutputMetricsFormatters.OfType<MetricsPrometheusProtobufOutputFormatter>().First();

				})
				.ConfigureWebHostDefaults(webBuilder =>
				{
					webBuilder.UseStartup<Startup>().
						ConfigureKestrel(x =>
						{

							x.ListenAnyIP(9091);
						});
					

				}).ConfigureServices((hostContext, services) =>
				{
					services.AddHostedService<MetricWriter>().AddHostedService<AppRunner>();});


			return hostBuilder;
		}
	}

	public class AppRunner : BackgroundService
	{
		private readonly IConfiguration _configuration;

		public AppRunner(IConfiguration configuration)
		{
			_configuration = configuration;
		}
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					var path = _configuration["CoreTempLogPath"];
					var proc = Process.Start(Path.Combine(path, "Core Temp.exe"));
					await Task.Delay(10000, stoppingToken);
					proc.Kill(true);
				}
				catch (Exception e)
				{
					Console.WriteLine(e);
				}

			}
		}
	}

	public class MetricWriter : BackgroundService
	{
		private readonly IConfiguration _configuration;
		private readonly IMetrics _metrics;

		public MetricWriter(IConfiguration configuration, IMetrics metrics)
		{
			_configuration = configuration;
			_metrics = metrics;
		}


		private string RemovePart(string s , string part)
		{
			if (string.IsNullOrWhiteSpace(s))
				return s;
			int i = s.LastIndexOf(part);
			if (i > 0)
			{
				s = s.Substring(0, i);
			}

			return s;
		}
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				var path = _configuration["CoreTempLogPath"];

				if (!Directory.Exists(path))
					throw new InvalidOperationException($"{path} not exists");
				var files = Directory.GetFiles(path, "*.csv").Select(x => new FileInfo(x));
				var dateFormat = _configuration["CoreTempDateFormat"];

				var current = files.OrderByDescending(x => x.CreationTime).First();
				var dataToParse = await FindLine(dateFormat, current.FullName);

				if (string.IsNullOrWhiteSpace(dataToParse.header) || string.IsNullOrWhiteSpace(dataToParse.line))
				{

					Console.WriteLine("no data");
					await Task.Delay(1000, stoppingToken);
					continue;
				}

				var headerlines = dataToParse.header.Split(new[] {','}, StringSplitOptions.None);
				var valueLines = dataToParse.line.Split(new[] {','}, StringSplitOptions.None);

				if (headerlines.Length != valueLines.Length)
				{
					Console.WriteLine("header mismatch");
				}

				string key = string.Empty;
				double value = 0;
				var lastKeyPrefix = string.Empty;
				for (int i = 0; i < headerlines.Length; i++)
				{
					string keyprefix = null;
					
					var keyK = headerlines[i].ToLower().Replace(" ", "_").Replace(")", "")
						.Replace("(", "").Replace("%", "percent").Replace("._?", string.Empty);

					if (keyK.StartsWith("core") && !keyK.Contains("temp") && !keyK.Contains("speed") && !keyK.Contains("load"))
					{
						lastKeyPrefix = keyK + "_";
						key = $"coretemp_{keyK}";
					}
					else if (keyK.StartsWith("core") && keyK.Contains("temp"))
					{
						keyprefix = keyK.Replace("temp", string.Empty);
						key = "temp";
					}
					else if (keyK.Contains("cpu_0_power"))
					{
						key = $"coretemp_{keyK}";
						lastKeyPrefix = string.Empty;
					}
					else if (string.IsNullOrWhiteSpace(keyK))
					{
						key = $"coretemp_default";

					}
					else
					{
						key = $"coretemp_{keyK}";
					
					
					}

					if (keyprefix==null)
					{
						keyprefix = lastKeyPrefix;
					}

					if (!string.IsNullOrWhiteSpace(keyprefix) && keyprefix.EndsWith("_"))
					{
						keyprefix = RemovePart(keyprefix, "_");
					}

					keyprefix = RemovePart(keyprefix, "._");

					key = RemovePart(key, "._");
					key = RemovePart(key, "_");


					value = 0;

					if (Double.TryParse(valueLines[i], out var val))
					{
						value = val;
					}

					MetricTags tags = new MetricTags();
					if (!string.IsNullOrWhiteSpace(keyprefix))
					{
						tags = new MetricTags("core", keyprefix);
					}

					_metrics.Measure.Gauge.SetValue(new GaugeOptions()
					{ 
						Name = key

					}, tags, value);
					Console.WriteLine($"{key} {value}");
					
				}

				await Task.Delay(1000, stoppingToken);
			}
		}


		private async Task<(string header, string line)> FindLine(string dateFormat, string path)
		{
			var date = DateTime.Now.AddHours(-24);
			Console.WriteLine(date.ToString(dateFormat));

			string line = null;
			DateTime last = DateTime.MinValue;

			string header = null;
			using (var stream = File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
				using (var reader = new StreamReader(stream) )
			{
				while (!reader.EndOfStream)
				{
					var s = await reader.ReadLineAsync();
					if (string.IsNullOrWhiteSpace(s))
					{
						continue;
						
					}
					var lines = s.Split(',');
					if (lines.Length > 0 && DateTime.TryParseExact(lines[0],dateFormat, new CultureInfo("en-US"), DateTimeStyles.None, out var dateValue))
					{
						if (last < dateValue)
						{
							line = s;
						}
						
					}

					if (s.StartsWith("Time,"))
					{
						header = s;
					}

				}

				
			}

			return (header, line);
		}

	}
}
