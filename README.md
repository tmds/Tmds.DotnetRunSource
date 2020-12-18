# dotnet-run-source

`dotnet-run-source` is a .NET tool for running a .NET application from sources in a repository.

Installing the tool:

```
dotnet tool install -g Tmds.DotnetRunSource --version '*-*' --add-source https://www.myget.org/F/tmds/api/v3/index.json
```

Example:
```
$ dotnet run-source --project app --branch dotnetcore-3.1 https://github.com/redhat-developer/s2i-dotnetcore-ex
```

