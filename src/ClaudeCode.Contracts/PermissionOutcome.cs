using System;
using System.Collections.Generic;

namespace ClaudeCode.Contracts;

/// <summary>
/// Specifies the possible outcomes of a permission request, including temporary and permanent allowances, rejections, and cancellations.
/// </summary>
public enum PermissionOutcome { AllowOnce, AllowAlways, RejectOnce, RejectAlways, Cancelled }
