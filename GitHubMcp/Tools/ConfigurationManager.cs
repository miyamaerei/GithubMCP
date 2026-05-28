using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitHubMcp.Tools;

/// <summary>
/// Git 认证类型枚举
/// </summary>
public enum GitAuthType
{
    Local = 0,
    Token = 1,
    Basic = 2
}

/// <summary>
/// 配置作用域枚举
/// </summary>
public enum ConfigScope
{
    Global = 0,
    User = 1,
    Project = 2
}

/// <summary>
/// 配置值类型枚举
/// </summary>
public enum ConfigValueType
{
    String = 0,
    Int = 1,
    Bool = 2,
    Json = 3
}

/// <summary>
/// 配置分组常量
/// </summary>
public static class ConfigGroups
{
    public const string Auth = "auth";
    public const string Git = "git";
    public const string Repo = "repo";
    public const string WorkingCopy = "workingcopy";
}

/// <summary>
/// 认证配置键常量
/// </summary>
public static class ConfigKeys
{
    public const string AuthType = "auth_type";
    public const string GitHubToken = "github_token";
    public const string GitHubEmail = "github_email";
    public const string GitToken = "git_token";
    public const string GitUsername = "git_username";
    public const string GitPassword = "git_password";
    public const string DefaultBranch = "default_branch";
    public const string IsConfigured = "is_configured";
}

/// <summary>
/// 单个配置项模型
/// </summary>
public class ConfigEntry
{
    public int Id { get; set; }
    public ConfigScope Scope { get; set; }
    public string? ScopeTarget { get; set; }
    public string Group { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public ConfigValueType ValueType { get; set; } = ConfigValueType.String;
    public bool IsEncrypted { get; set; } = false;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 统一配置请求模型
/// </summary>
public class SetConfigRequest
{
    public ConfigScope Scope { get; set; } = ConfigScope.User;
    public string? Target { get; set; }
    public GitAuthConfig? Config { get; set; }
}

/// <summary>
/// 配置列表项模型
/// </summary>
public class ConfigListItem
{
    public ConfigScope Scope { get; set; }
    public string? Target { get; set; }
    public string Group { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public ConfigValueType ValueType { get; set; }
    public bool IsEncrypted { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public GitAuthConfig? Config { get; set; }
}

/// <summary>
/// Git 认证配置结构
/// </summary>
public class GitAuthConfig
{
    public GitAuthType? AuthType { get; set; } = GitAuthType.Local;
    public string? GitHubToken { get; set; }
    public string? GitHubEmail { get; set; }
    public string? GitToken { get; set; }
    public string? GitUsername { get; set; }
    public string? GitPassword { get; set; }
    public string? DefaultBranch { get; set; } = "main";
    public bool IsConfigured { get; set; } = false;
}

/// <summary>
/// 配置来源信息
/// </summary>
public class ConfigSource
{
    public string Source { get; set; } = "全局默认配置";
    public string? ProjectPath { get; set; }
    public string? UserNtid { get; set; }
}

/// <summary>
/// 带来源信息的认证配置
/// </summary>
public class AuthConfigWithSource
{
    public GitAuthConfig Config { get; set; } = new();
    public ConfigSource Source { get; set; } = new();
    public Dictionary<string, string> FieldSources { get; set; } = new();
}

/// <summary>
/// 工作副本配置（数据库模型）
/// </summary>
public class WorkingCopy
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string OwnerNtid { get; set; } = "";
    public string? RemoteUrl { get; set; }
    public string? ProjectPath { get; set; }
    public bool IsDefault { get; set; } = false;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 配置管理器类，支持全局、用户、项目三级配置
/// </summary>
public class ConfigurationManager
{
    private readonly IConfiguration _configuration;
    private readonly string _dbPath;
    private readonly ILogger? _logger;

    private const string ConfigsTableName = "configs";
    private const string WorkingCopiesTableName = "working_copies";

    public ConfigurationManager(ILogger? logger = null)
    {
        _logger = logger;
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables();

        _configuration = builder.Build();
        _dbPath = GetDatabasePathInternal();
        EnsureAllTables();
        InitializeGlobalConfig();
    }

    private string GetDatabasePathInternal()
    {
        var envPath = Environment.GetEnvironmentVariable("GITHUB_MCP_DB_PATH");
        if (!string.IsNullOrEmpty(envPath))
        {
            return Path.IsPathRooted(envPath) ? envPath : Path.Combine(Directory.GetCurrentDirectory(), envPath);
        }

        var configPath = _configuration["Database:Path"];
        if (!string.IsNullOrEmpty(configPath))
        {
            return Path.IsPathRooted(configPath) ? configPath : Path.Combine(Directory.GetCurrentDirectory(), configPath);
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "mcp_users.db");
    }

    /// <summary>
    /// 确保所有配置表存在，并初始化全局配置
    /// </summary>
    private void EnsureAllTables()
    {
        try
        {
            var dbDir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }

            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS {ConfigsTableName} (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    scope INTEGER NOT NULL,
                    scope_target TEXT,
                    group_name TEXT NOT NULL,
                    key TEXT NOT NULL,
                    value TEXT,
                    value_type TEXT DEFAULT 'string',
                    is_encrypted INTEGER DEFAULT 0,
                    description TEXT,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(scope, scope_target, group_name, key)
                );

                CREATE TABLE IF NOT EXISTS {WorkingCopiesTableName} (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    local_path TEXT NOT NULL,
                    owner_ntid TEXT NOT NULL,
                    remote_url TEXT,
                    project_path TEXT,
                    is_default INTEGER DEFAULT 0,
                    updated_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(owner_ntid, name)
                );
            ";
            cmd.ExecuteNonQuery();
            _logger?.LogInformation("Database tables ensured at {DbPath}", _dbPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to ensure database tables");
            throw;
        }
    }

    /// <summary>
    /// 从 appsettings.json 加载初始全局配置
    /// </summary>
    private void InitializeGlobalConfig()
    {
        try
        {
            var authSection = _configuration.GetSection("GitAuth");
            if (!authSection.Exists())
            {
                _logger?.LogInformation("No GitAuth section in appsettings.json, using defaults");
                return;
            }

            var authTypeStr = authSection["AuthType"] ?? "Local";
            var authType = Enum.TryParse<GitAuthType>(authTypeStr, true, out var at) ? at : GitAuthType.Local;

            if (authType != GitAuthType.Local)
            {
                SetKeyValue(ConfigScope.Global, ConfigGroups.Auth, ConfigKeys.AuthType, ((int)authType).ToString());
            }

            var githubToken = authSection["GitHubToken"];
            if (!string.IsNullOrEmpty(githubToken))
            {
                SetKeyValue(ConfigScope.Global, ConfigGroups.Auth, ConfigKeys.GitHubToken, githubToken, isEncrypted: true);
            }

            var gitToken = authSection["GitToken"];
            if (!string.IsNullOrEmpty(gitToken))
            {
                SetKeyValue(ConfigScope.Global, ConfigGroups.Auth, ConfigKeys.GitToken, gitToken, isEncrypted: true);
            }

            var gitUsername = authSection["GitUsername"];
            if (!string.IsNullOrEmpty(gitUsername))
            {
                SetKeyValue(ConfigScope.Global, ConfigGroups.Auth, ConfigKeys.GitUsername, gitUsername);
            }

            var gitPassword = authSection["GitPassword"];
            if (!string.IsNullOrEmpty(gitPassword))
            {
                SetKeyValue(ConfigScope.Global, ConfigGroups.Auth, ConfigKeys.GitPassword, gitPassword, isEncrypted: true);
            }

            var defaultEmail = authSection["DefaultEmail"];
            if (!string.IsNullOrEmpty(defaultEmail))
            {
                SetKeyValue(ConfigScope.Global, ConfigGroups.Auth, ConfigKeys.GitHubEmail, defaultEmail);
            }

            var defaultBranch = authSection["DefaultBranch"];
            if (!string.IsNullOrEmpty(defaultBranch))
            {
                SetKeyValue(ConfigScope.Global, ConfigGroups.Auth, ConfigKeys.DefaultBranch, defaultBranch);
            }

            _logger?.LogInformation("Global config initialized from appsettings.json");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to initialize global config from appsettings.json, will use existing");
        }
    }

    /// <summary>
    /// 获取当前 Windows 用户的 NTID
    /// </summary>
    public string GetCurrentNtId()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent()?.Name ?? Environment.UserName;
            return id.Replace("\\", "_");
        }
        catch
        {
            return Environment.UserName;
        }
    }

    /// <summary>
    /// 获取当前 Windows 用户名
    /// </summary>
    private string GetCurrentWindowsUser()
    {
        try
        {
            return System.Security.Principal.WindowsIdentity.GetCurrent()?.Name ?? Environment.UserName;
        }
        catch
        {
            return Environment.UserName;
        }
    }

    /// <summary>
    /// 从 key-value 配置中获取认证配置
    /// </summary>
    private GitAuthConfig GetAuthConfigFromKeyValues(ConfigScope scope, string? target = null)
    {
        var config = new GitAuthConfig();

        var authTypeStr = GetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.AuthType, target);
        if (!string.IsNullOrEmpty(authTypeStr) && int.TryParse(authTypeStr, out var authTypeInt))
        {
            config.AuthType = (GitAuthType)authTypeInt;
        }

        var githubToken = GetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.GitHubToken, target);
        if (!string.IsNullOrEmpty(githubToken))
        {
            config.GitHubToken = githubToken;
        }

        var gitToken = GetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.GitToken, target);
        if (!string.IsNullOrEmpty(gitToken))
        {
            config.GitToken = gitToken;
        }

        var gitUsername = GetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.GitUsername, target);
        if (!string.IsNullOrEmpty(gitUsername))
        {
            config.GitUsername = gitUsername;
        }

        var gitPassword = GetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.GitPassword, target);
        if (!string.IsNullOrEmpty(gitPassword))
        {
            config.GitPassword = gitPassword;
        }

        var githubEmail = GetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.GitHubEmail, target);
        if (!string.IsNullOrEmpty(githubEmail))
        {
            config.GitHubEmail = githubEmail;
        }

        var defaultBranch = GetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.DefaultBranch, target);
        if (!string.IsNullOrEmpty(defaultBranch))
        {
            config.DefaultBranch = defaultBranch;
        }
        else
        {
            config.DefaultBranch = "main";
        }

        config.IsConfigured = !string.IsNullOrEmpty(githubToken) || !string.IsNullOrEmpty(gitToken) ||
                             !string.IsNullOrEmpty(gitUsername) || config.AuthType != GitAuthType.Local;

        return config;
    }

    /// <summary>
    /// 获取有效配置（按优先级：项目 > 用户 > 全局）
    /// </summary>
    public AuthConfigWithSource GetEffectiveConfig(string? projectPath = null)
    {
        var result = new AuthConfigWithSource
        {
            Config = new GitAuthConfig(),
            Source = new ConfigSource(),
            FieldSources = new Dictionary<string, string>()
        };

        var globalConfig = GetAuthConfigFromKeyValues(ConfigScope.Global);
        var ntid = GetCurrentNtId();
        var userConfig = GetAuthConfigFromKeyValues(ConfigScope.User, ntid);
        GitAuthConfig? projectConfig = null;

        if (!string.IsNullOrEmpty(projectPath))
        {
            projectConfig = GetAuthConfigFromKeyValues(ConfigScope.Project, projectPath);
        }

        if (projectConfig?.IsConfigured == true)
        {
            result.Source.Source = "项目配置";
            result.Source.ProjectPath = projectPath;
            result.Source.UserNtid = ntid;

            result.Config.AuthType = projectConfig.AuthType ?? userConfig.AuthType ?? globalConfig.AuthType;
            result.Config.GitHubToken = string.IsNullOrEmpty(projectConfig.GitHubToken) ? (string.IsNullOrEmpty(userConfig.GitHubToken) ? globalConfig.GitHubToken : userConfig.GitHubToken) : projectConfig.GitHubToken;
            result.Config.GitToken = string.IsNullOrEmpty(projectConfig.GitToken) ? (string.IsNullOrEmpty(userConfig.GitToken) ? globalConfig.GitToken : userConfig.GitToken) : projectConfig.GitToken;
            result.Config.GitUsername = string.IsNullOrEmpty(projectConfig.GitUsername) ? (string.IsNullOrEmpty(userConfig.GitUsername) ? globalConfig.GitUsername : userConfig.GitUsername) : projectConfig.GitUsername;
            result.Config.GitPassword = string.IsNullOrEmpty(projectConfig.GitPassword) ? (string.IsNullOrEmpty(userConfig.GitPassword) ? globalConfig.GitPassword : userConfig.GitPassword) : projectConfig.GitPassword;
            result.Config.GitHubEmail = string.IsNullOrEmpty(projectConfig.GitHubEmail) ? (string.IsNullOrEmpty(userConfig.GitHubEmail) ? globalConfig.GitHubEmail : userConfig.GitHubEmail) : projectConfig.GitHubEmail;
            result.Config.DefaultBranch = string.IsNullOrEmpty(projectConfig.DefaultBranch) ? globalConfig.DefaultBranch : projectConfig.DefaultBranch;
            result.Config.IsConfigured = true;

            result.FieldSources["auth_type"] = projectConfig.AuthType.HasValue ? "项目配置" : userConfig.AuthType.HasValue ? "用户配置" : "全局默认";
            result.FieldSources["github_token"] = !string.IsNullOrEmpty(projectConfig.GitHubToken) ? "项目配置" : !string.IsNullOrEmpty(userConfig.GitHubToken) ? "用户配置" : !string.IsNullOrEmpty(globalConfig.GitHubToken) ? "全局默认" : "未设置";
            result.FieldSources["git_token"] = !string.IsNullOrEmpty(projectConfig.GitToken) ? "项目配置" : !string.IsNullOrEmpty(userConfig.GitToken) ? "用户配置" : !string.IsNullOrEmpty(globalConfig.GitToken) ? "全局默认" : "未设置";
            result.FieldSources["git_username"] = !string.IsNullOrEmpty(projectConfig.GitUsername) ? "项目配置" : !string.IsNullOrEmpty(userConfig.GitUsername) ? "用户配置" : !string.IsNullOrEmpty(globalConfig.GitUsername) ? "全局默认" : "未设置";
            result.FieldSources["git_password"] = !string.IsNullOrEmpty(projectConfig.GitPassword) ? "项目配置" : !string.IsNullOrEmpty(userConfig.GitPassword) ? "用户配置" : !string.IsNullOrEmpty(globalConfig.GitPassword) ? "全局默认" : "未设置";
            result.FieldSources["github_email"] = !string.IsNullOrEmpty(projectConfig.GitHubEmail) ? "项目配置" : !string.IsNullOrEmpty(userConfig.GitHubEmail) ? "用户配置" : !string.IsNullOrEmpty(globalConfig.GitHubEmail) ? "全局默认" : "未设置";
            result.FieldSources["default_branch"] = !string.IsNullOrEmpty(projectConfig.DefaultBranch) ? "项目配置" : "全局默认";
        }
        else if (userConfig.IsConfigured)
        {
            result.Source.Source = "用户配置";
            result.Source.UserNtid = ntid;

            result.Config.AuthType = userConfig.AuthType;
            result.Config.GitHubToken = string.IsNullOrEmpty(userConfig.GitHubToken) ? globalConfig.GitHubToken : userConfig.GitHubToken;
            result.Config.GitToken = string.IsNullOrEmpty(userConfig.GitToken) ? globalConfig.GitToken : userConfig.GitToken;
            result.Config.GitUsername = string.IsNullOrEmpty(userConfig.GitUsername) ? globalConfig.GitUsername : userConfig.GitUsername;
            result.Config.GitPassword = string.IsNullOrEmpty(userConfig.GitPassword) ? globalConfig.GitPassword : userConfig.GitPassword;
            result.Config.GitHubEmail = string.IsNullOrEmpty(userConfig.GitHubEmail) ? globalConfig.GitHubEmail : userConfig.GitHubEmail;
            result.Config.DefaultBranch = globalConfig.DefaultBranch;
            result.Config.IsConfigured = true;

            result.FieldSources["auth_type"] = "用户配置";
            result.FieldSources["github_token"] = !string.IsNullOrEmpty(userConfig.GitHubToken) ? "用户配置" : "全局默认";
            result.FieldSources["git_token"] = !string.IsNullOrEmpty(userConfig.GitToken) ? "用户配置" : "全局默认";
            result.FieldSources["git_username"] = !string.IsNullOrEmpty(userConfig.GitUsername) ? "用户配置" : "全局默认";
            result.FieldSources["git_password"] = !string.IsNullOrEmpty(userConfig.GitPassword) ? "用户配置" : "全局默认";
            result.FieldSources["github_email"] = !string.IsNullOrEmpty(userConfig.GitHubEmail) ? "用户配置" : "全局默认";
            result.FieldSources["default_branch"] = "全局默认";
        }
        else
        {
            result.Source.Source = "全局默认配置";
            result.Config = globalConfig;

            result.FieldSources["auth_type"] = "全局默认";
            result.FieldSources["github_token"] = !string.IsNullOrEmpty(globalConfig.GitHubToken) ? "全局默认" : "未设置";
            result.FieldSources["git_token"] = !string.IsNullOrEmpty(globalConfig.GitToken) ? "全局默认" : "未设置";
            result.FieldSources["git_username"] = !string.IsNullOrEmpty(globalConfig.GitUsername) ? "全局默认" : "未设置";
            result.FieldSources["git_password"] = !string.IsNullOrEmpty(globalConfig.GitPassword) ? "全局默认" : "未设置";
            result.FieldSources["github_email"] = !string.IsNullOrEmpty(globalConfig.GitHubEmail) ? "全局默认" : "未设置";
            result.FieldSources["default_branch"] = "全局默认";
        }

        return result;
    }

    /// <summary>
    /// 统一设置配置（支持全局/用户/项目）
    /// </summary>
    public bool SetConfig(ConfigScope scope, string? target, GitAuthConfig config)
    {
        var ntid = GetCurrentNtId();
        var actualTarget = scope switch
        {
            ConfigScope.User => target ?? ntid,
            ConfigScope.Project => target ?? throw new ArgumentException("Project scope requires target (project path)"),
            _ => null
        };

        if (config.AuthType.HasValue)
        {
            SetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.AuthType, ((int)config.AuthType.Value).ToString(), false, actualTarget);
        }

        if (!string.IsNullOrEmpty(config.GitHubToken))
        {
            SetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.GitHubToken, config.GitHubToken, true, actualTarget);
        }

        if (!string.IsNullOrEmpty(config.GitToken))
        {
            SetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.GitToken, config.GitToken, true, actualTarget);
        }

        if (!string.IsNullOrEmpty(config.GitUsername))
        {
            SetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.GitUsername, config.GitUsername, false, actualTarget);
        }

        if (!string.IsNullOrEmpty(config.GitPassword))
        {
            SetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.GitPassword, config.GitPassword, true, actualTarget);
        }

        if (!string.IsNullOrEmpty(config.GitHubEmail))
        {
            SetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.GitHubEmail, config.GitHubEmail, false, actualTarget);
        }

        if (!string.IsNullOrEmpty(config.DefaultBranch))
        {
            SetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.DefaultBranch, config.DefaultBranch, false, actualTarget);
        }

        SetKeyValue(scope, ConfigGroups.Auth, ConfigKeys.IsConfigured, "true", false, actualTarget);

        return true;
    }

    /// <summary>
    /// 统一删除配置（支持用户/项目）
    /// </summary>
    public bool DeleteConfig(ConfigScope scope, string target)
    {
        if (scope == ConfigScope.Global)
        {
            throw new InvalidOperationException("Cannot delete global config, use DeleteAllConfigs instead");
        }

        return DeleteAllConfigs(scope, target);
    }

    /// <summary>
    /// 列出配置
    /// </summary>
    public List<ConfigListItem> ListConfigs(ConfigScope? scope = null)
    {
        var result = new List<ConfigListItem>();
        var ntid = GetCurrentNtId();

        if (scope == null || scope == ConfigScope.Global)
        {
            var globalEntries = GetAllConfigs(ConfigScope.Global);
            if (globalEntries.Any(e => e.Group == ConfigGroups.Auth))
            {
                var globalConfig = GetAuthConfigFromKeyValues(ConfigScope.Global);
                if (globalConfig.IsConfigured)
                {
                    result.Add(new ConfigListItem
                    {
                        Scope = ConfigScope.Global,
                        Target = null,
                        Group = ConfigGroups.Auth,
                        Key = "config",
                        Value = null,
                        Config = globalConfig,
                        UpdatedAt = globalEntries.FirstOrDefault()?.UpdatedAt
                    });
                }
            }
        }

        if (scope == null || scope == ConfigScope.User)
        {
            var userEntries = GetAllConfigs(ConfigScope.User, ntid);
            if (userEntries.Any(e => e.Group == ConfigGroups.Auth))
            {
                var userConfig = GetAuthConfigFromKeyValues(ConfigScope.User, ntid);
                if (userConfig.IsConfigured)
                {
                    result.Add(new ConfigListItem
                    {
                        Scope = ConfigScope.User,
                        Target = ntid,
                        Group = ConfigGroups.Auth,
                        Key = "config",
                        Value = null,
                        Config = userConfig,
                        UpdatedAt = userEntries.FirstOrDefault()?.UpdatedAt
                    });
                }
            }
        }

        if (scope == null || scope == ConfigScope.Project)
        {
            var projectEntries = GetAllConfigs(ConfigScope.Project);
            var projectGroups = projectEntries.Where(e => e.ScopeTarget != null).GroupBy(e => e.ScopeTarget);
            foreach (var group in projectGroups)
            {
                var projectPath = group.Key;
                var projectConfig = GetAuthConfigFromKeyValues(ConfigScope.Project, projectPath);
                if (projectConfig.IsConfigured)
                {
                    result.Add(new ConfigListItem
                    {
                        Scope = ConfigScope.Project,
                        Target = projectPath,
                        Group = ConfigGroups.Auth,
                        Key = "config",
                        Value = null,
                        Config = projectConfig,
                        UpdatedAt = group.FirstOrDefault()?.UpdatedAt
                    });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 获取工作副本
    /// </summary>
    public WorkingCopy? GetWorkingCopy(string name, string ntid)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id, name, local_path, owner_ntid, remote_url, project_path, is_default, updated_at FROM {WorkingCopiesTableName} WHERE name = $name AND owner_ntid = $ntid";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$ntid", ntid);
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                return new WorkingCopy
                {
                    Id = rdr.GetInt32(0),
                    Name = rdr.GetString(1),
                    LocalPath = rdr.GetString(2),
                    OwnerNtid = rdr.GetString(3),
                    RemoteUrl = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    ProjectPath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    IsDefault = rdr.GetInt32(6) == 1,
                    UpdatedAt = DateTime.Parse(rdr.GetString(7))
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get working copy {Name} for {Ntid}", name, ntid);
        }
        return null;
    }

    /// <summary>
    /// 根据 ID 获取工作副本
    /// </summary>
    public WorkingCopy? GetWorkingCopyById(int id)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id, name, local_path, owner_ntid, remote_url, project_path, is_default, updated_at FROM {WorkingCopiesTableName} WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                return new WorkingCopy
                {
                    Id = rdr.GetInt32(0),
                    Name = rdr.GetString(1),
                    LocalPath = rdr.GetString(2),
                    OwnerNtid = rdr.GetString(3),
                    RemoteUrl = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    ProjectPath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    IsDefault = rdr.GetInt32(6) == 1,
                    UpdatedAt = DateTime.Parse(rdr.GetString(7))
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get working copy by id {Id}", id);
        }
        return null;
    }

    /// <summary>
    /// 获取默认工作副本
    /// </summary>
    public WorkingCopy? GetDefaultWorkingCopy(string ntid)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id, name, local_path, owner_ntid, remote_url, project_path, is_default, updated_at FROM {WorkingCopiesTableName} WHERE owner_ntid = $ntid AND is_default = 1";
            cmd.Parameters.AddWithValue("$ntid", ntid);
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                return new WorkingCopy
                {
                    Id = rdr.GetInt32(0),
                    Name = rdr.GetString(1),
                    LocalPath = rdr.GetString(2),
                    OwnerNtid = rdr.GetString(3),
                    RemoteUrl = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    ProjectPath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    IsDefault = rdr.GetInt32(6) == 1,
                    UpdatedAt = DateTime.Parse(rdr.GetString(7))
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get default working copy for {Ntid}", ntid);
        }
        return null;
    }

    /// <summary>
    /// 列出用户的所有工作副本
    /// </summary>
    public List<WorkingCopy> ListWorkingCopies(string ntid)
    {
        var result = new List<WorkingCopy>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id, name, local_path, owner_ntid, remote_url, project_path, is_default, updated_at FROM {WorkingCopiesTableName} WHERE owner_ntid = $ntid ORDER BY is_default DESC, name ASC";
            cmd.Parameters.AddWithValue("$ntid", ntid);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                result.Add(new WorkingCopy
                {
                    Id = rdr.GetInt32(0),
                    Name = rdr.GetString(1),
                    LocalPath = rdr.GetString(2),
                    OwnerNtid = rdr.GetString(3),
                    RemoteUrl = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    ProjectPath = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    IsDefault = rdr.GetInt32(6) == 1,
                    UpdatedAt = DateTime.Parse(rdr.GetString(7))
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to list working copies for {Ntid}", ntid);
        }
        return result;
    }

    /// <summary>
    /// 保存工作副本
    /// </summary>
    public bool SaveWorkingCopy(WorkingCopy workingCopy)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            if (workingCopy.IsDefault)
            {
                using var clearCmd = conn.CreateCommand();
                clearCmd.CommandText = $"UPDATE {WorkingCopiesTableName} SET is_default = 0 WHERE owner_ntid = $ntid";
                clearCmd.Parameters.AddWithValue("$ntid", workingCopy.OwnerNtid);
                clearCmd.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"INSERT INTO {WorkingCopiesTableName} (name, local_path, owner_ntid, remote_url, project_path, is_default, updated_at)
                                 VALUES($name, $localPath, $ownerNtid, $remoteUrl, $projectPath, $isDefault, CURRENT_TIMESTAMP)
                                 ON CONFLICT(owner_ntid, name) DO UPDATE SET
                                     local_path = $localPath,
                                     remote_url = $remoteUrl,
                                     project_path = $projectPath,
                                     is_default = $isDefault,
                                     updated_at = CURRENT_TIMESTAMP";
            cmd.Parameters.AddWithValue("$name", workingCopy.Name);
            cmd.Parameters.AddWithValue("$localPath", workingCopy.LocalPath);
            cmd.Parameters.AddWithValue("$ownerNtid", workingCopy.OwnerNtid);
            cmd.Parameters.AddWithValue("$remoteUrl", string.IsNullOrEmpty(workingCopy.RemoteUrl) ? DBNull.Value : workingCopy.RemoteUrl);
            cmd.Parameters.AddWithValue("$projectPath", string.IsNullOrEmpty(workingCopy.ProjectPath) ? DBNull.Value : workingCopy.ProjectPath);
            cmd.Parameters.AddWithValue("$isDefault", workingCopy.IsDefault ? 1 : 0);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save working copy {Name} for {Ntid}", workingCopy.Name, workingCopy.OwnerNtid);
            return false;
        }
    }

    /// <summary>
    /// 删除工作副本
    /// </summary>
    public bool DeleteWorkingCopy(string name, string ntid)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {WorkingCopiesTableName} WHERE name = $name AND owner_ntid = $ntid";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$ntid", ntid);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete working copy {Name} for {Ntid}", name, ntid);
            return false;
        }
    }

    /// <summary>
    /// 设置默认工作副本
    /// </summary>
    public bool SetDefaultWorkingCopy(string name, string ntid)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {WorkingCopiesTableName} SET is_default = 0 WHERE owner_ntid = $ntid";
            cmd.Parameters.AddWithValue("$ntid", ntid);
            cmd.ExecuteNonQuery();

            using var setCmd = conn.CreateCommand();
            setCmd.CommandText = $"UPDATE {WorkingCopiesTableName} SET is_default = 1 WHERE name = $name AND owner_ntid = $ntid";
            setCmd.Parameters.AddWithValue("$name", name);
            setCmd.Parameters.AddWithValue("$ntid", ntid);
            setCmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set default working copy {Name} for {Ntid}", name, ntid);
            return false;
        }
    }

    /// <summary>
    /// 获取数据库路径
    /// </summary>
    public string GetDatabasePath() => _dbPath;

    #region Key-Value Config Methods

    /// <summary>
    /// 设置单个配置项
    /// </summary>
    public bool SetKeyValue(ConfigScope scope, string group, string key, string? value, bool isEncrypted = false, string? target = null, string? description = null)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {ConfigsTableName} (scope, scope_target, group_name, key, value, is_encrypted, description, updated_at)
                VALUES($scope, $target, $group, $key, $value, $encrypted, $desc, CURRENT_TIMESTAMP)
                ON CONFLICT(scope, scope_target, group_name, key) DO UPDATE SET
                    value = $value,
                    is_encrypted = $encrypted,
                    description = COALESCE($desc, description),
                    updated_at = CURRENT_TIMESTAMP";
            cmd.Parameters.AddWithValue("$scope", (int)scope);
            cmd.Parameters.AddWithValue("$target", target ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$group", group);
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$encrypted", isEncrypted ? 1 : 0);
            cmd.Parameters.AddWithValue("$desc", description ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set config: scope={Scope}, group={Group}, key={Key}", scope, group, key);
            return false;
        }
    }

    /// <summary>
    /// 获取单个配置项
    /// </summary>
    public string? GetKeyValue(ConfigScope scope, string group, string key, string? target = null)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT value, is_encrypted FROM {ConfigsTableName} WHERE scope = $scope AND scope_target IS $target AND group_name = $group AND key = $key";
            cmd.Parameters.AddWithValue("$scope", (int)scope);
            cmd.Parameters.AddWithValue("$target", target ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$group", group);
            cmd.Parameters.AddWithValue("$key", key);
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                var value = rdr.IsDBNull(0) ? null : rdr.GetString(0);
                var isEncrypted = rdr.GetInt32(1) == 1;
                return value;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get config: scope={Scope}, group={Group}, key={Key}", scope, group, key);
        }
        return null;
    }

    /// <summary>
    /// 删除单个配置项
    /// </summary>
    public bool DeleteKeyValue(ConfigScope scope, string group, string key, string? target = null)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {ConfigsTableName} WHERE scope = $scope AND scope_target IS $target AND group_name = $group AND key = $key";
            cmd.Parameters.AddWithValue("$scope", (int)scope);
            cmd.Parameters.AddWithValue("$target", target ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$group", group);
            cmd.Parameters.AddWithValue("$key", key);
            var rows = cmd.ExecuteNonQuery();
            return rows > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete config: scope={Scope}, group={Group}, key={Key}", scope, group, key);
            return false;
        }
    }

    /// <summary>
    /// 获取指定作用域的所有配置项
    /// </summary>
    public List<ConfigEntry> GetAllConfigs(ConfigScope scope, string? target = null)
    {
        var result = new List<ConfigEntry>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id, scope, scope_target, group_name, key, value, value_type, is_encrypted, description, created_at, updated_at FROM {ConfigsTableName} WHERE scope = $scope AND scope_target IS $target ORDER BY group_name, key";
            cmd.Parameters.AddWithValue("$scope", (int)scope);
            cmd.Parameters.AddWithValue("$target", target ?? (object)DBNull.Value);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                result.Add(new ConfigEntry
                {
                    Id = rdr.GetInt32(0),
                    Scope = (ConfigScope)rdr.GetInt32(1),
                    ScopeTarget = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    Group = rdr.GetString(3),
                    Key = rdr.GetString(4),
                    Value = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    ValueType = Enum.TryParse<ConfigValueType>(rdr.GetString(6), true, out var vt) ? vt : ConfigValueType.String,
                    IsEncrypted = rdr.GetInt32(7) == 1,
                    Description = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                    CreatedAt = DateTime.Parse(rdr.GetString(9)),
                    UpdatedAt = DateTime.Parse(rdr.GetString(10))
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get all configs for scope={Scope}", scope);
        }
        return result;
    }

    /// <summary>
    /// 获取指定分组的所有配置项
    /// </summary>
    public List<ConfigEntry> GetConfigsByGroup(ConfigScope scope, string group, string? target = null)
    {
        var result = new List<ConfigEntry>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id, scope, scope_target, group_name, key, value, value_type, is_encrypted, description, created_at, updated_at FROM {ConfigsTableName} WHERE scope = $scope AND scope_target IS $target AND group_name = $group ORDER BY key";
            cmd.Parameters.AddWithValue("$scope", (int)scope);
            cmd.Parameters.AddWithValue("$target", target ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$group", group);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                result.Add(new ConfigEntry
                {
                    Id = rdr.GetInt32(0),
                    Scope = (ConfigScope)rdr.GetInt32(1),
                    ScopeTarget = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    Group = rdr.GetString(3),
                    Key = rdr.GetString(4),
                    Value = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    ValueType = Enum.TryParse<ConfigValueType>(rdr.GetString(6), true, out var vt) ? vt : ConfigValueType.String,
                    IsEncrypted = rdr.GetInt32(7) == 1,
                    Description = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                    CreatedAt = DateTime.Parse(rdr.GetString(9)),
                    UpdatedAt = DateTime.Parse(rdr.GetString(10))
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get configs for group={Group} scope={Scope}", group, scope);
        }
        return result;
    }

    /// <summary>
    /// 列出所有配置（支持按作用域和分组过滤）
    /// </summary>
    public List<ConfigListItem> ListKeyValueConfigs(ConfigScope? scope = null, string? group = null)
    {
        var result = new List<ConfigListItem>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            var sql = $"SELECT scope, scope_target, group_name, key, value, value_type, is_encrypted, updated_at FROM {ConfigsTableName} WHERE 1=1";
            if (scope.HasValue) sql += " AND scope = $scope";
            if (!string.IsNullOrEmpty(group)) sql += " AND group_name = $group";
            sql += " ORDER BY scope, scope_target, group_name, key";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (scope.HasValue) cmd.Parameters.AddWithValue("$scope", (int)scope.Value);
            if (!string.IsNullOrEmpty(group)) cmd.Parameters.AddWithValue("$group", group);

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                result.Add(new ConfigListItem
                {
                    Scope = (ConfigScope)rdr.GetInt32(0),
                    Target = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    Group = rdr.GetString(2),
                    Key = rdr.GetString(3),
                    Value = rdr.IsDBNull(4) ? null : (rdr.GetInt32(6) == 1 ? "********" : rdr.GetString(4)),
                    ValueType = Enum.TryParse<ConfigValueType>(rdr.GetString(5), true, out var vt) ? vt : ConfigValueType.String,
                    IsEncrypted = rdr.GetInt32(6) == 1,
                    UpdatedAt = rdr.IsDBNull(7) ? null : DateTime.Parse(rdr.GetString(7))
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to list key-value configs");
        }
        return result;
    }

    /// <summary>
    /// 删除指定作用域的所有配置
    /// </summary>
    public bool DeleteAllConfigs(ConfigScope scope, string? target = null)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {ConfigsTableName} WHERE scope = $scope AND scope_target IS $target";
            cmd.Parameters.AddWithValue("$scope", (int)scope);
            cmd.Parameters.AddWithValue("$target", target ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete all configs for scope={Scope}", scope);
            return false;
        }
    }

    #endregion
}