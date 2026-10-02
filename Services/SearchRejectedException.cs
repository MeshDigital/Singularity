using System;
using Singularity.Models;

namespace Singularity.Services;

public class SearchRejectedException : Exception
{
    public SearchAttemptLog? SearchLog { get; }

    public SearchRejectedException(string message, SearchAttemptLog? log = null) : base(message)
    {
        SearchLog = log;
    }
}
