// msfastbuild.cs - Generates and executes a bff file for fastbuild from a .sln or .vcxproj.
// Copyright 2016 Liam Flookes and Yassine Riahi
// Available under an MIT license. See license file on github for details.
using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine;
using CommandLine.Text;
using System.IO;
using System.Reflection;
using System.Text;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Construction;
using Microsoft.Build.Utilities;

namespace msfastbuild
{
	public class Options
	{
		[Option('p', "vcproject", DefaultValue = "",
		HelpText = "Path of .vcxproj file to build, or project name if a solution is provided.")]
		public string Project { get; set; }

		[Option('s', "sln", DefaultValue = "",
		HelpText = "Path of .sln file which contains the projects.")]
		public string Solution { get; set; }

		[Option('c', "config", DefaultValue = "Debug",
		HelpText = "Configuration to build.")]
		public string Config { get; set; }

		[Option('f', "platform", DefaultValue = "Win32",
		HelpText = "Platform to build.")]
		public string Platform { get; set; }

		[Option('a', "fbargs", DefaultValue = "-dist",
		HelpText = "Arguments that pass through to FASTBuild.")]
		public string FBArgs { get; set; }

		[Option('g', "generateonly", DefaultValue = false,
		HelpText = "Generate bff file only, without calling FASTBuild.")]
		public bool GenerateOnly { get; set; }

		[Option('r', "regen", DefaultValue = false,
		HelpText = "Regenerate bff file even when the project hasn't changed.")]
		public bool AlwaysRegenerate { get; set; }

		[Option('b', "fbpath", DefaultValue = @"FBuild.exe",
		HelpText = "Path to FASTBuild executable.")]
		public string FBPath { get; set; }

		[Option('u', "unity", DefaultValue = false,
		HelpText = "Whether to combine files into a unity step. May substantially improve compilation time, but not all projects are suitable.")]
		public bool UseUnity { get; set; }

		[Option('q', "quiet", DefaultValue = false,
		HelpText = "Force disabling output of FASTBuild.")]
		public bool QuietMode { get; set; }

		[Option('R', "rebuild", DefaultValue = false,
		HelpText = "Rebuild project FASTBuild.")]
		public bool Rebuild { get; set; }

		[HelpOption]
		public string GetUsage()
		{
			return HelpText.AutoBuild(this,(HelpText current) => HelpText.DefaultParsingErrorsHandler(this, current));
		}
	}

	public class msfastbuild
	{
		static public string PlatformToolsetVersion = "140";
		static public string VCBasePath = "";
		static public string VCExePath = "";
		static public Options CommandLineOptions = new Options();
		static public string WindowsSDKTarget = "10.0.10240.0";
		static public MSFBProject CurrentProject;
		static public Assembly CPPTasksAssembly;
		static public string PreBuildBatchFile = "";
        static public int CustomBuildIndex = -1;
		static public string PostBuildBatchFile = "";
		static public string SolutionDir = "";
		static public bool HasCompileActions = true;
		static public System.Diagnostics.Process FBProcess;

		public enum BuildType
		{
		    Application,
		    StaticLib,
		    DynamicLib
		}

		static public BuildType BuildOutput = BuildType.Application;

		public class MSFBProject
		{
			public Project Proj;
			public List<MSFBProject> Dependents = new List<MSFBProject>();
			public string AdditionalLinkInputs = "";
			public List<MSFBProject> AdditionalDependencies = new List<MSFBProject>();
			public string BFFFilePath = "";
		}

		static void Main(string[] args)
		{
			Parser parser = new Parser();
			if (!parser.ParseArguments(args, CommandLineOptions))
			{
				Console.WriteLine(CommandLineOptions.GetUsage());
				return;
			}

			if (string.IsNullOrEmpty(CommandLineOptions.Solution) && string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				Console.WriteLine("No solution or project provided!");
				Console.WriteLine(CommandLineOptions.GetUsage());
				return;
			}

			List<string> ProjectsToBuild = new List<string>();
			List<string> AllProjects = new List<string>();
			Dictionary<string, List<string>> slnDependencies = new Dictionary<string, List<string>>();
			if (!string.IsNullOrEmpty(CommandLineOptions.Solution) && File.Exists(CommandLineOptions.Solution))
			{
				try
				{
					List<ProjectInSolution> SolutionProjects = SolutionFile.Parse(Path.GetFullPath(CommandLineOptions.Solution)).ProjectsInOrder.Where(el => el.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat).ToList();
					foreach (var proj in SolutionProjects)
                    {
						slnDependencies.Add(proj.ProjectGuid, proj.Dependencies.ToList());
                    }
					AllProjects = SolutionProjects.ConvertAll(el => el.AbsolutePath);
					if (string.IsNullOrEmpty(CommandLineOptions.Project))
					{
						ProjectsToBuild.AddRange(AllProjects);
					}
					else
					{
						ProjectsToBuild.Add(Path.GetFullPath(CommandLineOptions.Project));
					}

					SolutionDir = Path.GetDirectoryName(Path.GetFullPath(CommandLineOptions.Solution));
					SolutionDir = SolutionDir.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					if(SolutionDir.Last() != Path.AltDirectorySeparatorChar)
						SolutionDir += Path.AltDirectorySeparatorChar;
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to parse solution file " + CommandLineOptions.Solution + "!");
					Console.WriteLine("Exception: " + e.Message);
					return;
				}
			}
			else if (!string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				ProjectsToBuild.Add(Path.GetFullPath(CommandLineOptions.Project));
			}

			var AllProjectList = new List<MSFBProject>();
			var ProjectGuidDict = new Dictionary<string, MSFBProject>();
			foreach (var p in AllProjects)
			{
				var proj = ParseProject(p);
				if (proj == null)
					continue;
				AllProjectList.Add(proj);
				var projectGuid = proj.Proj.GetPropertyValue("ProjectGuid");
				ProjectGuidDict[projectGuid] = proj;
			}

			foreach (var proj in AllProjectList)
            {
				var projectGuid = proj.Proj.GetPropertyValue("ProjectGuid");
                if (slnDependencies.TryGetValue(projectGuid, out List<string> dependencies))
                {
                    foreach (var dep in dependencies)
                    {
                        if (ProjectGuidDict.TryGetValue(dep, out MSFBProject dependency))
                        {
							proj.AdditionalDependencies.Add(dependency);
                        }
                    }
                }
            }

			var EvaluatedProjects = new List<MSFBProject>();

			foreach (var p in ProjectsToBuild)
            {
				EvaluateProjectReferences(p, AllProjectList, EvaluatedProjects, null);
            }

			var Graph = new Dictionary<MSFBProject, List<MSFBProject>>();
			foreach (var proj in EvaluatedProjects)
            {
				var tempDependents = new List<MSFBProject>();
                tempDependents.AddRange(proj.Dependents);
                tempDependents.AddRange(proj.AdditionalDependencies);
                Graph.Add(proj, tempDependents);
            }

			EvaluatedProjects = TopologicalSortKahn(Graph);
			EvaluatedProjects.Reverse();

			int ProjectsBuilt = 0;
			var WaitToBuild = new List<MSFBProject>();
			foreach(MSFBProject project in EvaluatedProjects)
			{
				CurrentProject = project;

				string VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPathEffective");
				if (string.IsNullOrEmpty(VCTargetsPath))
				{
					VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPath");
				}
				if (string.IsNullOrEmpty(VCTargetsPath))
				{
					Console.WriteLine("Failed to evaluate VCTargetsPath variable on " + Path.GetFileName(CurrentProject.Proj.FullPath) + "!");
					continue;
				}

				bool useBuiltinDll = true;
				string BuildDllName = "Microsoft.Build.CPPTasks.Common.dll";
				string BuildDllPath = VCTargetsPath + BuildDllName;
				if (File.Exists(BuildDllPath))
				{
					CPPTasksAssembly = Assembly.LoadFrom(BuildDllPath);
					if (CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL") != null &&
						CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.RC") != null &&
						CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link") != null &&
						CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB") != null)
					{
						useBuiltinDll = false;
					}
				}
				if (useBuiltinDll)
				{
					CPPTasksAssembly = Assembly.LoadFrom(AppDomain.CurrentDomain.BaseDirectory + BuildDllName);
				}

				CurrentProject.BFFFilePath = Path.GetDirectoryName(CurrentProject.Proj.FullPath) + "\\" + Path.GetFileName(CurrentProject.Proj.FullPath) + "_" + CommandLineOptions.Config.Replace(" ", "") + "_" + CommandLineOptions.Platform.Replace(" ", "") + ".bff";
				GenerateBffFromVcxproj(CommandLineOptions.Config, CommandLineOptions.Platform);

				if (!CommandLineOptions.GenerateOnly)
				{
					if (HasCompileActions)
					{
						WaitToBuild.Add(CurrentProject);
					}
				}
			}

			if (!CommandLineOptions.GenerateOnly)
			{
				foreach (var proj in WaitToBuild)
				{
					if (!ExecuteBffFile(proj.Proj.FullPath, proj.BFFFilePath, CommandLineOptions.Platform))
						break;
					ProjectsBuilt++;
				}
			}
			
			Console.WriteLine(ProjectsBuilt + "/" + EvaluatedProjects.Count + " built.");
		}

		public static List<T> TopologicalSortKahn<T>(Dictionary<T, List<T>> graph)
		{
			// 收集所有节点（包括没有出边的节点）
			var allNodes = new HashSet<T>();
			foreach (var node in graph.Keys)
			{
				allNodes.Add(node);
				if (graph.TryGetValue(node, out var neighbors))
				{
					foreach (var neighbor in neighbors)
					{
						allNodes.Add(neighbor);
					}
				}
			}

			// 计算每个节点的入度
			var inDegree = new Dictionary<T, int>();
			foreach (var node in allNodes)
			{
				inDegree[node] = 0;
			}

			foreach (var node in graph)
			{
				foreach (var neighbor in node.Value)
				{
					inDegree[neighbor]++;
				}
			}

			// 将所有入度为0的节点加入队列
			var queue = new Queue<T>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
			var result = new List<T>();

			while (queue.Count > 0)
			{
				var node = queue.Dequeue();
				result.Add(node);

				// 减少其邻居节点的入度
				if (graph.TryGetValue(node, out var neighbors))
				{
					foreach (var neighbor in neighbors)
					{
						inDegree[neighbor]--;
						if (inDegree[neighbor] == 0)
						{
							queue.Enqueue(neighbor);
						}
					}
				}
			}

			// 如果排序结果的节点数不等于图的节点数，说明存在环
			if (result.Count != allNodes.Count)
			{
				throw new InvalidOperationException("Graph contains a cycle");
			}

			return result;
		}

		static public MSFBProject ParseProject(string ProjectPath)
		{
			if (!string.IsNullOrEmpty(ProjectPath) && File.Exists(ProjectPath))
			{
				try
				{
					ProjectCollection projColl = new ProjectCollection();
					if (!string.IsNullOrEmpty(SolutionDir))
						projColl.SetGlobalProperty("SolutionDir", SolutionDir);
					var newProj = new MSFBProject();
					Project proj = projColl.LoadProject(ProjectPath);

					if (proj != null)
					{
						proj.SetGlobalProperty("Configuration", CommandLineOptions.Config);
						proj.SetGlobalProperty("Platform", CommandLineOptions.Platform);
						if (!string.IsNullOrEmpty(SolutionDir))
							proj.SetGlobalProperty("SolutionDir", SolutionDir);
						proj.ReevaluateIfNecessary();
						newProj.Proj = proj;
						return newProj;
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to parse project file " + ProjectPath + "!");
					Console.WriteLine("Exception: " + e.Message);
				}
			}
			return null;
		}

		static public void EvaluateProjectReferences(string ProjectPath, List<MSFBProject> allProjects, List<MSFBProject> evaluatedProjects, MSFBProject dependent)
		{
			if (!string.IsNullOrEmpty(ProjectPath) && File.Exists(ProjectPath))
			{
				try
				{
					var FullPath = Path.GetFullPath(ProjectPath);
					MSFBProject newProj = evaluatedProjects.Find(elem => elem.Proj.FullPath == FullPath);
					if (newProj != null)
					{
						//Console.WriteLine("Found exisiting project " + Path.GetFileNameWithoutExtension(ProjectPath));
						if (dependent != null)
							newProj.Dependents.Add(dependent);
					}
					else
					{
						newProj = allProjects.Find(e => e.Proj.FullPath == FullPath);
						if (dependent != null)
						{
							newProj.Dependents.Add(dependent);
						}
						var ProjectReferences = newProj.Proj.Items.Where(elem => elem.ItemType == "ProjectReference");
						foreach (var ProjRef in ProjectReferences)
						{
							if (ProjRef.GetMetadataValue("ReferenceOutputAssembly") == "true" || ProjRef.GetMetadataValue("LinkLibraryDependencies") == "true")
							{
								//Console.WriteLine(string.Format("{0} referenced by {1}.", Path.GetFileNameWithoutExtension(ProjRef.EvaluatedInclude), Path.GetFileNameWithoutExtension(proj.FullPath)));
								EvaluateProjectReferences(ProjRef.EvaluatedInclude, allProjects, evaluatedProjects, newProj);
							}
						}
                        //Console.WriteLine("Adding " + Path.GetFileNameWithoutExtension(proj.FullPath));
                        evaluatedProjects.Add(newProj);
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to parse project file " + ProjectPath + "!");
					Console.WriteLine("Exception: " + e.Message);
					return;
				}
			}
		}

		static public bool HasFileChanged(string InputFile, string Platform, string Config, out string MD5hash)
		{
			using (var md5 = System.Security.Cryptography.MD5.Create())
			{
				using (var stream = File.OpenRead(InputFile))
				{
				    MD5hash = ";" + InputFile + "_" + Platform + "_" + Config + "_" + BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLower();
				}
			}
			
			if (!File.Exists(CurrentProject.BFFFilePath))
				return true;
			
			try
            {
                using (var reader = new StreamReader(CurrentProject.BFFFilePath))
                {
                    var FirstLine = reader.ReadLine();
                    return FirstLine != MD5hash;
                }
            }
            catch (Exception e)
            {
                return true;
            }
		}

		static public bool ExecuteBffFile(string ProjectPath, string BFFFilePath, string Platform)
		{
			string projectDir = Path.GetDirectoryName(ProjectPath) + "\\";
			Console.WriteLine("Building " + Path.GetFileNameWithoutExtension(ProjectPath));
			try
			{
				FBProcess = new System.Diagnostics.Process();
				FBProcess.StartInfo.FileName = CommandLineOptions.FBPath;
				FBProcess.StartInfo.Arguments = "-config \"" + BFFFilePath + "\" " + CommandLineOptions.FBArgs;
				if (CommandLineOptions.Rebuild)
                {
					FBProcess.StartInfo.Arguments += " -clean";
                }
				FBProcess.StartInfo.RedirectStandardOutput = true;
				FBProcess.StartInfo.UseShellExecute = false;
				FBProcess.StartInfo.WorkingDirectory = projectDir;
				FBProcess.StartInfo.StandardOutputEncoding = Console.OutputEncoding;

				FBProcess.Start();
				while (!FBProcess.StandardOutput.EndOfStream)
				{
				    Console.Write(FBProcess.StandardOutput.ReadLine() + "\n");
				}
				FBProcess.WaitForExit();
				bool result = FBProcess.ExitCode == 0;
				FBProcess.Dispose();
				FBProcess = null;
				return result;
			}
			catch (Exception e)
			{
				Console.WriteLine("Failed to launch FASTBuild!");
				Console.WriteLine("Exception: " + e.Message);
				return false;
			}
		}

		public class ObjectListNode
		{
			string Compiler;
			string CompilerOutputPath;
			string CompilerOptions;
			string CompilerOutputExtension;
			string PrecompiledHeaderString;

			List<string> CompilerInputFiles;
		
			public ObjectListNode(string InputFile, string InCompiler, string InCompilerOutputPath, string InCompilerOptions, string InPrecompiledHeaderString, string InCompilerOutputExtension = "")
			{
				CompilerInputFiles = new List<string>();
				CompilerInputFiles.Add(InputFile);
				Compiler = InCompiler;
				CompilerOutputPath = InCompilerOutputPath;
				CompilerOptions = InCompilerOptions;
				CompilerOutputExtension = InCompilerOutputExtension;
				PrecompiledHeaderString = InPrecompiledHeaderString;
			}
		
			public bool AddIfMatches(string InputFile, string InCompiler, string InCompilerOutputPath, string InCompilerOptions, string InPrecompiledHeaderString)
			{
				if(Compiler == InCompiler && CompilerOutputPath == InCompilerOutputPath && CompilerOptions == InCompilerOptions && PrecompiledHeaderString == InPrecompiledHeaderString)
				{
					CompilerInputFiles.Add(InputFile);
					return true;
				}
				return false;
			}
		
			public string ToString(int ActionNumber)
			{
				bool UsedUnity = false;
				string ResultString = "";
				if(CommandLineOptions.UseUnity && Compiler != "rc" && CompilerInputFiles.Count > 1)
				{
					StringBuilder UnityListString = new StringBuilder(string.Format("Unity('unity_{0}')\n{{\n", ActionNumber));
					UnityListString.AppendFormat("\t.UnityInputFiles = {{ {0} }}\n", string.Join(",", CompilerInputFiles.ConvertAll(el => string.Format("'{0}'", el)).ToArray()));
					UnityListString.AppendFormat("\t.UnityOutputPath = \"{0}\"\n", CompilerOutputPath);
					UnityListString.AppendFormat("\t.UnityNumFiles = {0}\n", 1 + CompilerInputFiles.Count/10);
					UnityListString.Append("}\n\n");
					UsedUnity = true;
					ResultString = UnityListString.ToString();
				}

				StringBuilder ObjectListString = new StringBuilder(string.Format("ObjectList('action_{0}')\n{{\n", ActionNumber));
				ObjectListString.AppendFormat("\t.Compiler = '{0}'\n", Compiler);
				ObjectListString.AppendFormat("\t.CompilerOutputPath = \"{0}\"\n", CompilerOutputPath);
				if(UsedUnity)
				{
					ObjectListString.AppendFormat("\t.CompilerInputUnity = {{ {0} }}\n", string.Format("'unity_{0}'", ActionNumber));
				}
				else
				{
					ObjectListString.AppendFormat("\t.CompilerInputFiles = {{ {0} }}\n", string.Join(",", CompilerInputFiles.ConvertAll(el => string.Format("'{0}'", el)).ToArray()));
				}				
				ObjectListString.AppendFormat("\t.CompilerOptions = '{0}'\n", CompilerOptions);
				if (!string.IsNullOrEmpty(CompilerOutputExtension))
				{
					ObjectListString.AppendFormat("\t.CompilerOutputExtension = '{0}'\n", CompilerOutputExtension);
				}
				if (!string.IsNullOrEmpty(PrecompiledHeaderString))
				{
					ObjectListString.Append(PrecompiledHeaderString);
				}
                if (CustomBuildIndex > -1)
                {
                    ObjectListString.AppendFormat("\t.PreBuildDependencies  = 'custombuild_{0}'\n", CustomBuildIndex);
                }
                else if (!string.IsNullOrEmpty(PreBuildBatchFile))
                {
                    ObjectListString.Append("\t.PreBuildDependencies  = 'prebuild'\n");
                }

				ObjectListString.Append("}\n\n");
				ResultString += ObjectListString.ToString();
				return ResultString;
			}
		}

		static private void AddExtraDlls(StringBuilder outputString, string rootDir, string pattern)
		{
			string[] dllFiles = Directory.GetFiles(rootDir, pattern);
			foreach (string dllFile in dllFiles)
			{
				outputString.AppendFormat("\t\t'$Root$/{0}'\n", Path.GetFileName(dllFile));
			}
		}

		static private void GenerateBffFromVcxproj(string Config, string Platform)
		{
			CustomBuildIndex = -1;
			Project ActiveProject = CurrentProject.Proj;
			string MD5hash = "wafflepalooza";
			PreBuildBatchFile = "";
			PostBuildBatchFile = "";
			bool FileChanged = HasFileChanged(ActiveProject.FullPath, Platform, Config, out MD5hash);

			if (!FileChanged && CommandLineOptions.Rebuild && CommandLineOptions.AlwaysRegenerate)
				return;

			string configType = ActiveProject.GetProperty("ConfigurationType").EvaluatedValue;
			switch(configType)
			{
				case "DynamicLibrary": BuildOutput = BuildType.DynamicLib; break;
				case "StaticLibrary": BuildOutput = BuildType.StaticLib; break;
				default:
				case "Application": BuildOutput = BuildType.Application; break;				
			}

			PlatformToolsetVersion = ActiveProject.GetProperty("PlatformToolsetVersion").EvaluatedValue;

			string OutDir = ActiveProject.GetProperty("OutDir").EvaluatedValue;
			string IntDir = ActiveProject.GetProperty("IntDir").EvaluatedValue;

			StringBuilder OutputString = new StringBuilder(MD5hash + "\n\n");

			OutputString.AppendFormat(".VSBasePath = '{0}'\n", ActiveProject.GetProperty("VSInstallDir").EvaluatedValue);
			VCBasePath = ActiveProject.GetProperty("VCInstallDir").EvaluatedValue;
			OutputString.AppendFormat(".VCBasePath = '{0}'\n", VCBasePath);

			if (Platform == "Win32" || Platform == "x86")
			{
				VCExePath = ActiveProject.GetProperty("VC_ExecutablePath_x86_x86").EvaluatedValue;
			}
			else
			{
				VCExePath = ActiveProject.GetProperty("VC_ExecutablePath_x64_x64").EvaluatedValue;
			}
			OutputString.AppendFormat(".VCExePath = '{0}'\n", VCExePath );

			WindowsSDKTarget = ActiveProject.GetProperty("WindowsTargetPlatformVersion") != null ? ActiveProject.GetProperty("WindowsTargetPlatformVersion").EvaluatedValue : "8.1";

			string winSdkDir = ActiveProject.GetProperty("WindowsSdkDir").EvaluatedValue;
			OutputString.AppendFormat(".WindowsSDKBasePath = '{0}'\n\n", winSdkDir);

			OutputString.Append("Settings\n{\n\t.Environment = \n\t{\n");
			OutputString.AppendFormat("\t\t\"INCLUDE={0}\",\n", ActiveProject.GetProperty("IncludePath").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"LIB={0}\",\n", ActiveProject.GetProperty("LibraryPath").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"LIBPATH={0}\",\n", ActiveProject.GetProperty("ReferencePath").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"PATH={0}\"\n", ActiveProject.GetProperty("Path").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"TMP={0}\"\n", ActiveProject.GetProperty("Temp").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"TEMP={0}\"\n", ActiveProject.GetProperty("Temp").EvaluatedValue);
			OutputString.AppendFormat("\t\t\"SystemRoot={0}\"\n", ActiveProject.GetProperty("SystemRoot").EvaluatedValue);
			OutputString.Append("\t}\n}\n\n");

			StringBuilder CompilerString = new StringBuilder("Compiler('msvc')\n{\n");

			string CompilerRoot = VCExePath;
			CompilerString.Append("\t.Root = '$VCExePath$'\n");
			CompilerString.Append("\t.Executable = '$Root$/cl.exe'\n");
			CompilerString.Append("\t.ExtraFiles =\n\t{\n");
			CompilerString.Append("\t\t'$Root$/c1.dll'\n");
			CompilerString.Append("\t\t'$Root$/c1xx.dll'\n");
			CompilerString.Append("\t\t'$Root$/c2.dll'\n");

			if(File.Exists(CompilerRoot + "1033/clui.dll")) //Check English first...
			{
				CompilerString.Append("\t\t'$Root$/1033/clui.dll'\n");
			}
			else
			{
				var numericDirectories = Directory.GetDirectories(CompilerRoot).Where(d => Path.GetFileName(d).All(char.IsDigit));
				var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
				if(cluiDirectories.Any())
				{
					CompilerString.AppendFormat("\t\t'$Root$/{0}/clui.dll'\n", Path.GetFileName(cluiDirectories.First()));
				}
			}
			
			CompilerString.Append("\t\t'$Root$/mspdbsrv.exe'\n");
			//CompilerString.Append("\t\t'$Root$/mspdbcore.dll'\n");

			//CompilerString.AppendFormat("\t\t'$Root$/mspft{0}.dll'\n", PlatformToolsetVersion);
			//CompilerString.AppendFormat("\t\t'$Root$/msobj{0}.dll'\n", PlatformToolsetVersion);
			//CompilerString.AppendFormat("\t\t'$Root$/mspdb{0}.dll'\n", PlatformToolsetVersion);
			//CompilerString.AppendFormat("\t\t'$VSBasePath$/VC/redist/{0}/Microsoft.VC{1}.CRT/msvcp{1}.dll'\n", Platform == "Win32" ? "x86" : "x64", PlatformToolsetVersion);
			//CompilerString.AppendFormat("\t\t'$VSBasePath$/VC/redist/{0}/Microsoft.VC{1}.CRT/vccorlib{1}.dll'\n", Platform == "Win32" ? "x86" : "x64", PlatformToolsetVersion);

			AddExtraDlls(CompilerString, CompilerRoot, "msobj*.dll");
			AddExtraDlls(CompilerString, CompilerRoot, "mspdb*.dll");
			AddExtraDlls(CompilerString, CompilerRoot, "mspft*.dll");
			AddExtraDlls(CompilerString, CompilerRoot, "msvcp*.dll");
			AddExtraDlls(CompilerString, CompilerRoot, "tbbmalloc.dll");
			AddExtraDlls(CompilerString, CompilerRoot, "vcmeta.dll");
			AddExtraDlls(CompilerString, CompilerRoot, "vcruntime*.dll");

			CompilerString.Append("\t}\n"); //End extra files
			CompilerString.Append("}\n\n"); //End compiler

			string rcPath = "\\bin\\" + WindowsSDKTarget + "\\x64\\rc.exe";
			if (!File.Exists(winSdkDir + rcPath))
			{
				rcPath = "\\bin\\x64\\rc.exe";
			}

			CompilerString.Append("Compiler('rc')\n{\n");
			CompilerString.Append("\t.Executable = '$WindowsSDKBasePath$" + rcPath + "'\n");
			CompilerString.Append("\t.CompilerFamily = 'custom'\n");
			CompilerString.Append("}\n\n"); //End rc compiler

			OutputString.Append(CompilerString);

			if (ActiveProject.GetItems("PreBuildEvent").Any())
			{
				var buildEvent = ActiveProject.GetItems("PreBuildEvent").First();
				if (buildEvent.Metadata.Any())
				{
					var mdPi = buildEvent.Metadata.First();
					if(!string.IsNullOrEmpty(mdPi.EvaluatedValue))
					{
						string BatchText = "call \"" + VCBasePath + "Auxiliary\\Build\\vcvarsall.bat\" "
							+ (Platform == "Win32" ? "x86" : "x64") + " " + WindowsSDKTarget + "\n";
						PreBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_prebuild.bat");
						File.WriteAllText(PreBuildBatchFile, BatchText + mdPi.EvaluatedValue);
						OutputString.Append("Exec('prebuild') \n{\n");
						OutputString.AppendFormat("\t.ExecExecutable = '{0}' \n", PreBuildBatchFile);
						OutputString.AppendFormat("\t.ExecInput = '{0}' \n", PreBuildBatchFile);
						OutputString.AppendFormat("\t.ExecOutput = '{0}' \n", PreBuildBatchFile + ".txt");
						OutputString.Append("\t.ExecUseStdOutAsOutput = true \n");
						OutputString.Append("}\n\n");
					}
				}
			}

			string CompilerOptions = "";

			List<ObjectListNode> ObjectLists = new List<ObjectListNode>();
			var CompileItems = ActiveProject.GetItems("ClCompile");
			string PrecompiledHeaderString = "";

			foreach (var Item in CompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
					{
						ToolTask CLtask = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
						CLtask.GetType().GetProperty("Sources").SetValue(CLtask, new TaskItem[] { new TaskItem() });
						string pchCompilerOptions = GenerateTaskCommandLine(CLtask, new string[] { "PrecompiledHeaderOutputFile", "ObjectFileName", "AssemblerListingLocation" }, Item.Metadata) + " /FS";
						PrecompiledHeaderString = "\t.PCHOptions = '" + string.Format("\"%1\" /Fp\"%2\" /Fo\"%3\" {0} '\n", pchCompilerOptions);
						PrecompiledHeaderString += "\t.PCHInputFile = '" + Item.EvaluatedInclude + "'\n";
						PrecompiledHeaderString += "\t.PCHOutputFile = '" + Item.GetMetadataValue("PrecompiledHeaderOutputFile") + "'\n";
						break; //Assumes only one pch...
					}
				}
			}

			foreach (var Item in CompileItems)
			{
				bool ExcludePrecompiledHeader = false;
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "NotUsing").Any())
						ExcludePrecompiledHeader = true;
				}

				ToolTask Task = (ToolTask) Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
				Task.GetType().GetProperty("Sources").SetValue(Task, new TaskItem[] { new TaskItem() }); //CPPTasks throws an exception otherwise...
				string TempCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ObjectFileName", "AssemblerListingLocation" }, Item.Metadata) + " /FS";
				if (Path.GetExtension(Item.EvaluatedInclude) == ".c")
					TempCompilerOptions += " /TC";
				else
					TempCompilerOptions += " /TP";
				CompilerOptions = TempCompilerOptions;
				string FormattedCompilerOptions = string.Format("\"%1\" /Fo\"%2\" {0}", TempCompilerOptions);
				var MatchingNodes = ObjectLists.Where(el => el.AddIfMatches(Item.EvaluatedInclude, "msvc", IntDir, FormattedCompilerOptions, ExcludePrecompiledHeader ? "" : PrecompiledHeaderString));
				if(!MatchingNodes.Any())
				{
					ObjectLists.Add(new ObjectListNode(Item.EvaluatedInclude, "msvc", IntDir, FormattedCompilerOptions, ExcludePrecompiledHeader ? "" : PrecompiledHeaderString));
				}
			}

			PrecompiledHeaderString = "";

			var ResourceCompileItems = ActiveProject.GetItems("ResourceCompile");
			foreach (var Item in ResourceCompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
				}
			
				ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.RC"));
				string ResourceCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ResourceOutputFileName", "DesigntimePreprocessorDefinitions" }, Item.Metadata);
			
				string formattedCompilerOptions = string.Format("{0} /fo\"%2\" \"%1\"", ResourceCompilerOptions);
				var MatchingNodes = ObjectLists.Where(el => el.AddIfMatches(Item.EvaluatedInclude, "rc", IntDir, formattedCompilerOptions, PrecompiledHeaderString));
				if (!MatchingNodes.Any())
				{
					ObjectLists.Add(new ObjectListNode(Item.EvaluatedInclude, "rc", IntDir, formattedCompilerOptions, PrecompiledHeaderString, ".res"));
				}
			}

            var CustomBuilds = ActiveProject.GetItems("CustomBuild");
            var Dependencies = new List<string>();
            var CustomBuildBatchText = new List<string>();
            var CustomBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_custom.bat");
            CustomBuildBatchText.Add("@echo off");
            CustomBuildBatchText.Add("call \"" + VCBasePath + "Auxiliary\\Build\\vcvarsall.bat\" "
                    + (Platform == "Win32" ? "x86" : "x64") + " " + WindowsSDKTarget + "\n");
            foreach (var Item in CustomBuilds)
            {
                if (Item.DirectMetadata.Any())
                {
                    if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
                        continue;
                }
                if (!Item.Metadata.Where(dmd => dmd.Name == "Command").Any())
                {
                    continue;
                }

                var File = Path.Combine(ActiveProject.DirectoryPath, Item.EvaluatedInclude);
                Dependencies.Add(File);
                var Command = Item.Metadata.Where(dmd => dmd.Name == "Command").First().EvaluatedValue;

                CustomBuildBatchText.Add(string.Format("if \"%1\"==\"custombuild_{0}\" (", CustomBuildIndex + 1));
                CustomBuildBatchText.Add('\t' + Command);
                CustomBuildBatchText.Add(")");

                var AdditionInputs = Item.Metadata.Where(dmd => dmd.Name == "AdditionalInputs");
                var Dependices = new List<string>();
                Dependices.Add(Path.Combine(ActiveProject.DirectoryPath, Item.EvaluatedInclude));
                if (AdditionInputs.Any())
                {
                    Dependices.AddRange(AdditionInputs.First().EvaluatedValue.Split(';'));
                }

                OutputString.AppendFormat("Exec('custombuild_{0}')\n", CustomBuildIndex + 1);
                OutputString.Append("{");
                OutputString.AppendFormat("\n\t.ExecExecutable = '{0}'", CustomBuildBatchFile);
                var StartFlag = true;
                OutputString.Append("\n\t.ExecInput = {");
                foreach (var tempFile in Dependices)
                {
                    if (string.IsNullOrEmpty(tempFile))
                        continue;
                    if (!StartFlag)
                        OutputString.Append(",");
                    OutputString.Append("'");
                    OutputString.Append(tempFile);
                    OutputString.Append("'");
                    StartFlag = false;
                }
                OutputString.Append("}");
                OutputString.AppendFormat("\n\t.ExecArguments = 'custombuild_{0}'", CustomBuildIndex + 1);
                var Outputs = Item.Metadata.Where(dmd => dmd.Name == "Outputs");
                if (Outputs.Any())
                {
                    OutputString.AppendFormat("\n\t.ExecOutput = '{0}'", Outputs.First().EvaluatedValue.Trim(new char[] { ';' }));
                }
                OutputString.Append("\n\t.ExecUseStdOutAsOutput = false");
                if (CustomBuildIndex == -1)
                {
                    if (!string.IsNullOrEmpty(PreBuildBatchFile))
                    {
                        OutputString.AppendFormat("\n\t.PreBuildDependencies = 'prebuild'");
                    }
                }
                else
                {
                    OutputString.AppendFormat("\n\t.PreBuildDependencies = 'custombuild_{0}'", CustomBuildIndex);
                }
                OutputString.Append("\n}\n\n");

                CustomBuildIndex += 1;
            }
            if (CustomBuildIndex > -1 && (FileChanged || CommandLineOptions.AlwaysRegenerate || CommandLineOptions.Rebuild || !File.Exists(CustomBuildBatchFile)))
            {
                File.WriteAllLines(CustomBuildBatchFile, CustomBuildBatchText);
            }

			int ActionNumber = 0;
			foreach (ObjectListNode ObjList in ObjectLists)
			{
				OutputString.Append(ObjList.ToString(ActionNumber));
				ActionNumber++;		
			}

			if (ActionNumber > 0)
			{
				HasCompileActions = true;
			}
			else
			{
				HasCompileActions = false;
				Console.WriteLine("Project has no actions to compile.");
			}

			string CompileActions = string.Join(",", Enumerable.Range(0, ActionNumber).ToList().ConvertAll(x => string.Format("'action_{0}'", x)).ToArray());

			if (BuildOutput == BuildType.Application || BuildOutput == BuildType.DynamicLib)
			{
				OutputString.AppendFormat("{0}('output')\n{{", BuildOutput == BuildType.Application ? "Executable" : "DLL");
				OutputString.Append("\t.Linker = '$VCExePath$\\link.exe'\n");
		
				var LinkDefinitions = ActiveProject.ItemDefinitions["Link"];
				string OutputFile = LinkDefinitions.GetMetadataValue("OutputFile").Replace('\\', '/');

				if(HasCompileActions && BuildOutput != BuildType.Application)
				{
					string DependencyOutputPath = LinkDefinitions.GetMetadataValue("ImportLibrary");
					if (Path.IsPathRooted(DependencyOutputPath))
						DependencyOutputPath = DependencyOutputPath.Replace('\\', '/');
					else
						DependencyOutputPath = Path.Combine(ActiveProject.DirectoryPath, DependencyOutputPath).Replace('\\', '/');

					foreach (var deps in CurrentProject.Dependents)
					{
						deps.AdditionalLinkInputs += " \"" + DependencyOutputPath + "\" ";
					}
				}

				ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link"));
				string LinkerOptions = GenerateTaskCommandLine(Task, new string[] { "OutputFile", "ProfileGuidedDatabase" }, LinkDefinitions.Metadata);

				if (!string.IsNullOrEmpty(CurrentProject.AdditionalLinkInputs))
				{
					LinkerOptions += CurrentProject.AdditionalLinkInputs;
				}
				OutputString.AppendFormat("\t.LinkerOptions = '\"%1\" /OUT:\"%2\" {0}'\n", LinkerOptions.Replace("'","^'"));
				OutputString.AppendFormat("\t.LinkerOutput = '{0}'\n", OutputFile);

				OutputString.Append("\t.Libraries = { ");
				OutputString.Append(CompileActions);
				OutputString.Append(" }\n");

				OutputString.Append("}\n\n");
			}
			else if(BuildOutput == BuildType.StaticLib)
			{
				OutputString.Append("Library('output')\n{");
				OutputString.Append("\t.Compiler = 'msvc'\n");
				OutputString.Append(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c {0}'\n", CompilerOptions));
				OutputString.Append(string.Format("\t.CompilerOutputPath = \"{0}\"\n", IntDir));
				OutputString.Append("\t.Librarian = '$VCExePath$\\lib.exe'\n");

				var LibDefinitions = ActiveProject.ItemDefinitions["Lib"];
				string OutputFile = LibDefinitions.GetMetadataValue("OutputFile").Replace('\\','/');

				if(HasCompileActions)
				{
					string DependencyOutputPath = "";
					if (Path.IsPathRooted(OutputFile))
						DependencyOutputPath = Path.GetFullPath(OutputFile).Replace('\\', '/');
					else
						DependencyOutputPath = Path.Combine(ActiveProject.DirectoryPath, OutputFile).Replace('\\', '/');

					foreach (var deps in CurrentProject.Dependents)
					{
						deps.AdditionalLinkInputs += " \"" + DependencyOutputPath + "\" ";
					}
				}

				ToolTask task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB"));
				string linkerOptions = GenerateTaskCommandLine(task, new string[] { "OutputFile" }, LibDefinitions.Metadata);
				if(!string.IsNullOrEmpty(CurrentProject.AdditionalLinkInputs))
				{
					linkerOptions += CurrentProject.AdditionalLinkInputs;
				}
				OutputString.AppendFormat("\t.LibrarianOptions = '\"%1\" /OUT:\"%2\" {0}'\n", linkerOptions);
				OutputString.AppendFormat("\t.LibrarianOutput = '{0}'\n", OutputFile);

				OutputString.Append("\t.LibrarianAdditionalInputs = { ");
				OutputString.Append(CompileActions);
				OutputString.Append(" }\n");

				OutputString.Append("}\n\n");
			}

			if (ActiveProject.GetItems("PostBuildEvent").Any())
			{
				ProjectItem BuildEvent = ActiveProject.GetItems("PostBuildEvent").First();
				if (BuildEvent.Metadata.Any())
				{
					ProjectMetadata MetaData = BuildEvent.Metadata.First();
					if(!string.IsNullOrEmpty(MetaData.EvaluatedValue))
					{
						string BatchText = "call \"" + VCBasePath + "Auxiliary\\Build\\vcvarsall.bat\" "
							+ (Platform == "Win32" ? "x86" : "x64") + " " + WindowsSDKTarget + "\n";
						PostBuildBatchFile = Path.Combine(ActiveProject.DirectoryPath, Path.GetFileNameWithoutExtension(ActiveProject.FullPath) + "_postbuild.bat");
						File.WriteAllText(PostBuildBatchFile, BatchText + MetaData.EvaluatedValue);
						OutputString.Append("Exec('postbuild') \n{\n");
						OutputString.AppendFormat("\t.ExecExecutable = '{0}' \n", PostBuildBatchFile);
						OutputString.AppendFormat("\t.ExecInput = '{0}' \n", PostBuildBatchFile);
						OutputString.AppendFormat("\t.ExecOutput = '{0}' \n", PostBuildBatchFile + ".txt");
						OutputString.Append("\t.PreBuildDependencies = 'output' \n");
						OutputString.Append("\t.ExecUseStdOutAsOutput = true \n");
						OutputString.Append("}\n\n");
					}
				}
			}

			OutputString.AppendFormat("Alias ('all')\n{{\n\t.Targets = {{ '{0}' }}\n}}", string.IsNullOrEmpty(PostBuildBatchFile) ? "output" : "postbuild");

			if(FileChanged || CommandLineOptions.AlwaysRegenerate || CommandLineOptions.Rebuild)
			{
				File.WriteAllText(CurrentProject.BFFFilePath, OutputString.ToString());
			}		   
		}

		public static string GenerateTaskCommandLine(
			ToolTask Task,
			string[] PropertiesToSkip,
			IEnumerable<ProjectMetadata> MetaDataList)
		{
			foreach (ProjectMetadata MetaData in MetaDataList)
			{
				if (PropertiesToSkip.Contains(MetaData.Name))
					continue;

				var MatchingProps = Task.GetType().GetProperties().Where(prop => prop.Name == MetaData.Name);
				if (MatchingProps.Any() && !string.IsNullOrEmpty(MetaData.EvaluatedValue))
				{
					string EvaluatedValue = MetaData.EvaluatedValue.Trim();
					if(MetaData.Name == "AdditionalIncludeDirectories")
					{
						EvaluatedValue = EvaluatedValue.Replace("\\\\", "\\");
						EvaluatedValue = EvaluatedValue.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					}

					PropertyInfo propInfo = MatchingProps.First(); //Dubious
					if (propInfo.PropertyType.IsArray && propInfo.PropertyType.GetElementType() == typeof(string))
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue.Split(';'), propInfo.PropertyType));
					}
					else
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue, propInfo.PropertyType));
					}
				}
			}

			var GenCmdLineMethod = Task.GetType().GetRuntimeMethods().Where(meth => meth.Name == "GenerateCommandLine").First(); //Dubious
			return GenCmdLineMethod.Invoke(Task, new object[] { Type.Missing, Type.Missing }) as string;
		}
	}

}
