using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Specifies the possible states of a tool call operation.
/// </summary>
public enum ToolCallStatus { Pending, InProgress, Completed, Failed }
