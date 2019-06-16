﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace BepInEx
{
	/// <summary>
	/// Generic helper properties and methods.
	/// </summary>
	public static class Utility
	{
		/// <summary>
		/// Combines multiple paths together, as the specific method is not available in .NET 3.5.
		/// </summary>
		/// <param name="parts">The multiple paths to combine together.</param>
		/// <returns>A combined path.</returns>
		public static string CombinePaths(params string[] parts) => parts.Aggregate(Path.Combine);

		/// <summary>
		/// Tries to parse a bool, with a default value if unable to parse.
		/// </summary>
		/// <param name="input">The string to parse</param>
		/// <param name="defaultValue">The value to return if parsing is unsuccessful.</param>
		/// <returns>Boolean value of input if able to be parsed, otherwise default value.</returns>
		public static bool SafeParseBool(string input, bool defaultValue = false) { return Boolean.TryParse(input, out bool result) ? result : defaultValue; }

		/// <summary>
		/// Converts a file path into a UnityEngine.WWW format.
		/// </summary>
		/// <param name="path">The file path to convert.</param>
		/// <returns>A converted file path.</returns>
		public static string ConvertToWWWFormat(string path) { return $"file://{path.Replace('\\', '/')}"; }

		/// <summary>
		/// Indicates whether a specified string is null, empty, or consists only of white-space characters.
		/// </summary>
		/// <param name="self">The string to test.</param>
		/// <returns>True if the value parameter is null or empty, or if value consists exclusively of white-space characters.</returns>
		public static bool IsNullOrWhiteSpace(this string self) { return self == null || self.All(Char.IsWhiteSpace); }

		public static IEnumerable<TNode> TopologicalSort<TNode>(IEnumerable<TNode> nodes, Func<TNode, IEnumerable<TNode>> dependencySelector)
		{
			List<TNode> sorted_list = new List<TNode>();

			HashSet<TNode> visited = new HashSet<TNode>();
			HashSet<TNode> sorted = new HashSet<TNode>();

			foreach (TNode input in nodes)
			{
				Stack<TNode> currentStack = new Stack<TNode>();
				if (!Visit(input, currentStack))
				{
					throw new Exception("Cyclic Dependency:\r\n" + currentStack.Select(x => $" - {x}") //append dashes
																			   .Aggregate((a, b) => $"{a}\r\n{b}")); //add new lines inbetween
				}
			}


			return sorted_list;

			bool Visit(TNode node, Stack<TNode> stack)
			{
				if (visited.Contains(node))
				{
					if (!sorted.Contains(node))
					{
						return false;
					}
				}
				else
				{
					visited.Add(node);

					stack.Push(node);

					foreach (var dep in dependencySelector(node))
						if (!Visit(dep, stack))
							return false;


					sorted.Add(node);
					sorted_list.Add(node);

					stack.Pop();
				}

				return true;
			}
		}

		/// <summary>
		/// Try to resolve and load the given assembly DLL.
		/// </summary>
		/// <param name="assemblyName">Name of the assembly, of the type <see cref="AssemblyName" />.</param>
		/// <param name="directory">Directory to search the assembly from.</param>
		/// <param name="assembly">The loaded assembly.</param>
		/// <returns>True, if the assembly was found and loaded. Otherwise, false.</returns>
		private static bool TryResolveDllAssembly<T>(AssemblyName assemblyName, string directory, Func<string, T> loader, out T assembly) where T : class
		{
			assembly = null;

			var potentialDirectories = new List<string> { directory };

			potentialDirectories.AddRange(Directory.GetDirectories(directory, "*", SearchOption.AllDirectories));

			foreach (string subDirectory in potentialDirectories)
			{
				string path = Path.Combine(subDirectory, $"{assemblyName.Name}.dll");

				if (!File.Exists(path))
					continue;

				try
				{
					assembly = loader(path);
				}
				catch (Exception)
				{
					continue;
				}

				return true;
			}

			return false;
		}

		public static bool IsSubtypeOf(this TypeDefinition self, Type td)
		{
			if (self.FullName == td.FullName)
				return true;
			return self.FullName != "System.Object" && (self.BaseType?.Resolve().IsSubtypeOf(td) ?? false);
		}

		/// <summary>
        /// Try to resolve and load the given assembly DLL.
        /// </summary>
        /// <param name="assemblyName">Name of the assembly, of the type <see cref="AssemblyName" />.</param>
        /// <param name="directory">Directory to search the assembly from.</param>
        /// <param name="assembly">The loaded assembly.</param>
        /// <returns>True, if the assembly was found and loaded. Otherwise, false.</returns>
        public static bool TryResolveDllAssembly(AssemblyName assemblyName, string directory, out Assembly assembly) { return TryResolveDllAssembly(assemblyName, directory, Assembly.LoadFile, out assembly); }

		/// <summary>
		/// Try to resolve and load the given assembly DLL.
		/// </summary>
		/// <param name="assemblyName">Name of the assembly, of the type <see cref="AssemblyName" />.</param>
		/// <param name="directory">Directory to search the assembly from.</param>
		/// <param name="assembly">The loaded assembly.</param>
		/// <returns>True, if the assembly was found and loaded. Otherwise, false.</returns>
		public static bool TryResolveDllAssembly(AssemblyName assemblyName, string directory, ReaderParameters readerParameters, out AssemblyDefinition assembly) { return TryResolveDllAssembly(assemblyName, directory, s => AssemblyDefinition.ReadAssembly(s, readerParameters), out assembly); }
	}
}