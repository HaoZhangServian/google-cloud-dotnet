﻿// Copyright 2016 Google Inc. All Rights Reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using DotLiquid.Exceptions;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Google.GCloud.Tools.GenerateSnippetMarkdown
{
    /// <summary>
    /// A snippet from the snippets directory.
    /// </summary>
    public class Snippet
    {
        /// <summary>
        /// The snippet ID that appears in source code. This is specified in a "Snippet:",
        /// "Sample:" or "Resource:" line.
        /// </summary>
        public string SnippetId { get; set; }

        /// <summary>
        /// Members to resolve in metadata, i.e. code members to use this snippet in as sample code.
        /// </summary>
        public IList<string> MetadataMembers { get; } = new List<string>();

        /// <summary>
        /// The UIDs of the docfx metadata items, if any.
        /// </summary>
        public IList<string> MetadataUids { get; } = new List<string>();

        /// <summary>
        /// Where in the snippet text this snippet starts (inclusive).
        /// </summary>
        public int StartLine { get; set; }

        /// <summary>
        /// Where in the snippet text this snippet ends (inclusive).
        /// </summary>
        public int EndLine { get; set; }

        /// <summary>
        /// Lines of the snippet.
        /// </summary>
        public List<string> Lines { get; } = new List<string>();

        /// <summary>
        /// File containing the snippet
        /// </summary>
        public string SourceFile { get; set; }

        /// <summary>
        /// First line of the snippet within the source file.
        /// </summary>
        public int SourceStartLine { get; set; }

        /// <summary>
        /// Formatted SourceFile:SourceStartLine.
        /// </summary>
        public string SourceLocation => $"{SourceFile}:{SourceStartLine}";

        /// <summary>
        /// The line used to indicate the start of this snippet in the output text file, for docfx to pick up.
        /// </summary>
        public string DocfxSnippetStart => Type.FormatStart(SnippetId);

        /// <summary>
        /// The line used to indicate the end of this snippet in the output text file, for docfx to pick up.
        /// </summary>
        public string DocfxSnippetEnd => Type.FormatEnd(SnippetId);

        // Note: this will throw an exception if the extension isn't known. It would be nice to provide the
        // error in a more graceful way, but this'll do for now.
        private SnippetType Type => SnippetType.FromFilename(SourceFile);

        private sealed class SnippetType
        {
            private static readonly SnippetType CSharpType = new SnippetType("// <{0}>", "// </{0}>");
            private static readonly SnippetType XmlType = new SnippetType("<!-- <{0}> -->", "<!-- </{0}> -->");
            private readonly string startFormat;
            private readonly string endFormat;

            private SnippetType(string startFormat, string endFormat)
            {
                this.startFormat = startFormat;
                this.endFormat = endFormat;
            }

            internal static SnippetType FromFilename(string file)
            {
                switch (Path.GetExtension(file))
                {
                    case ".cs":
                        return CSharpType;
                    case ".xml":
                        return XmlType;
                    default:
                        throw new ArgumentException($"No known snippet type for file {file}");
                }
            }

            internal string FormatStart(string snippetId) => string.Format(startFormat, snippetId);
            internal string FormatEnd(string snippetId) => string.Format(endFormat, snippetId);
        }

        /// <summary>
        /// Trim all leading spaces by a uniform amount (the smallest number of spaces in any line).
        /// </summary>
        internal void TrimLeadingSpaces()
        {
            var spacesToRemove = Lines.Min(line => line.Trim() == "" ? int.MaxValue : line.TakeWhile(c => c == ' ').Count());
            for (int i = 0; i < Lines.Count; i++)
            {
                Lines[i] = Lines[i].Trim() == "" ? "": Lines[i].Substring(spacesToRemove);
            }
        }
    }
}
