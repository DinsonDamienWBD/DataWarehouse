// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.Security;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Security;

/// <summary>
/// Comprehensive tests for multi-level access control hierarchy (CommandIdentity + AccessVerificationMatrix).
/// </summary>
public sealed class AccessVerificationMatrixTests
{
    [Fact]
    public void DefaultDeny_NoRules_ReturnsDefaultDeny()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();
        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" });

        // Act
        var verdict = matrix.Evaluate(identity, "resource:file", "read");

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.RuleId.Should().Be("default-deny");
        verdict.Reason.Should().Contain("No access rules found");
    }

    [Fact]
    public void SystemLevelDeny_OverridesAllOtherAllows()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        // System deny
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "system-maintenance",
            Level = HierarchyLevel.System,
            ScopeId = "system",
            Resource = "*",
            Action = "*",
            Decision = LevelDecision.Deny,
            Description = "System maintenance mode"
        });

        // User allow (should be ignored)
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "user-allow",
            Level = HierarchyLevel.User,
            ScopeId = "user:alice",
            Resource = "*",
            Action = "*",
            Decision = LevelDecision.Allow
        });

        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "admin" });

        // Act
        var verdict = matrix.Evaluate(identity, "resource:file", "read");

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.DecidedAtLevel.Should().Be(HierarchyLevel.System);
        verdict.RuleId.Should().Be("system-maintenance");
        verdict.Reason.Should().Contain("System maintenance mode");
    }

    [Fact]
    public void TenantLevelDeny_OverridesInstanceGroupUserAllows()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        // Tenant deny
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "tenant-suspended",
            Level = HierarchyLevel.Tenant,
            ScopeId = "tenant1",
            Resource = "*",
            Action = "*",
            Decision = LevelDecision.Deny,
            Description = "Tenant account suspended"
        });

        // User allow (should be ignored)
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "user-allow",
            Level = HierarchyLevel.User,
            ScopeId = "user:alice",
            Resource = "*",
            Action = "*",
            Decision = LevelDecision.Allow
        });

        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "admin" });

        // Act
        var verdict = matrix.Evaluate(identity, "resource:file", "write");

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.DecidedAtLevel.Should().Be(HierarchyLevel.Tenant);
        verdict.RuleId.Should().Be("tenant-suspended");
        verdict.Reason.Should().Contain("Tenant account suspended");
    }

    [Fact]
    public void UserLevelAllow_WithNoDenies_ReturnsAllow()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "user-can-read",
            Level = HierarchyLevel.User,
            ScopeId = "user:alice",
            Resource = "resource:file",
            Action = "read",
            Decision = LevelDecision.Allow,
            Description = "User alice can read files"
        });

        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" });

        // Act
        var verdict = matrix.Evaluate(identity, "resource:file", "read");

        // Assert
        verdict.Allowed.Should().BeTrue();
        verdict.DecidedAtLevel.Should().Be(HierarchyLevel.User);
        verdict.RuleId.Should().Be("user-can-read");
        verdict.Reason.Should().Contain("User alice can read files");
    }

    [Fact]
    public void DenyTakesPrecedence_UserAllowPlusGroupDeny_ReturnsDeny()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        // User allow
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "user-allow",
            Level = HierarchyLevel.User,
            ScopeId = "user:alice",
            Resource = "resource:file",
            Action = "delete",
            Decision = LevelDecision.Allow
        });

        // Group deny (takes precedence)
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "group-deny-delete",
            Level = HierarchyLevel.UserGroup,
            ScopeId = "group:readonly",
            Resource = "resource:file",
            Action = "delete",
            Decision = LevelDecision.Deny,
            Description = "Readonly group cannot delete"
        });

        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" },
            groupIds: new[] { "group:readonly" });

        // Act
        var verdict = matrix.Evaluate(identity, "resource:file", "delete");

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.DecidedAtLevel.Should().Be(HierarchyLevel.UserGroup);
        verdict.RuleId.Should().Be("group-deny-delete");
        verdict.Reason.Should().Contain("Readonly group cannot delete");
    }

    [Fact]
    public void AiDelegationDeny_AiAgentWithoutPermission_ReturnsDeny()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        // User has no permission
        var userIdentity = CommandIdentity.ForUser(
            userId: "bob",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" });

        var aiIdentity = CommandIdentity.ForAiAgent("gemini-user", userIdentity);

        // Act
        var verdict = matrix.Evaluate(aiIdentity, "resource:file", "read");

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.RuleId.Should().Be("default-deny");
        aiIdentity.OnBehalfOfPrincipalId.Should().Be("user:bob");
        aiIdentity.EffectivePrincipalId.Should().Be("user:bob");
    }

    [Fact]
    public void AiDelegationAllow_AiAgentActingForAdmin_ReturnsAllow()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "admin-full-access",
            Level = HierarchyLevel.User,
            ScopeId = "user:admin",
            Resource = "*",
            Action = "*",
            Decision = LevelDecision.Allow,
            Description = "Admin has full access"
        });

        var adminIdentity = CommandIdentity.ForUser(
            userId: "admin",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "admin" });

        var aiIdentity = CommandIdentity.ForAiAgent("claude-system", adminIdentity);

        // Act
        var verdict = matrix.Evaluate(aiIdentity, "resource:database", "write");

        // Assert
        verdict.Allowed.Should().BeTrue();
        verdict.DecidedAtLevel.Should().Be(HierarchyLevel.User);
        verdict.RuleId.Should().Be("admin-full-access");
        aiIdentity.OnBehalfOfPrincipalId.Should().Be("user:admin");
        aiIdentity.EffectivePrincipalId.Should().Be("user:admin");
    }

    [Fact]
    public void ChainDelegation_UserToGeminiToClaude_PreservesOriginalUser()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "alice-read-access",
            Level = HierarchyLevel.User,
            ScopeId = "user:alice",
            Resource = "resource:file",
            Action = "read",
            Decision = LevelDecision.Allow
        });

        var userIdentity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" });

        var geminiIdentity = CommandIdentity.ForAiAgent("gemini-user", userIdentity);
        var claudeIdentity = geminiIdentity.WithDelegation("ai:claude-system");

        // Act
        var verdict = matrix.Evaluate(claudeIdentity, "resource:file", "read");

        // Assert
        verdict.Allowed.Should().BeTrue();
        claudeIdentity.OnBehalfOfPrincipalId.Should().Be("user:alice");
        claudeIdentity.EffectivePrincipalId.Should().Be("user:alice");
        claudeIdentity.DelegationChain.Should().Contain("ai:gemini-user");
        claudeIdentity.DelegationChain.Should().Contain("ai:claude-system");
        claudeIdentity.DelegationChain.Count.Should().Be(2);
    }

    [Fact]
    public void ExpiredRuleIgnored_ExpiredAllowDoesNotApply()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "expired-allow",
            Level = HierarchyLevel.User,
            ScopeId = "user:alice",
            Resource = "resource:file",
            Action = "read",
            Decision = LevelDecision.Allow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) // Expired yesterday
        });

        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" });

        // Act
        var verdict = matrix.Evaluate(identity, "resource:file", "read");

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.RuleId.Should().Be("default-deny");
    }

    [Fact]
    public void WildcardResource_MatchesAllResources()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "admin-all-resources",
            Level = HierarchyLevel.User,
            ScopeId = "user:admin",
            Resource = "*", // Wildcard matches all
            Action = "read",
            Decision = LevelDecision.Allow
        });

        var identity = CommandIdentity.ForUser(
            userId: "admin",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "admin" });

        // Act
        var verdict1 = matrix.Evaluate(identity, "resource:file", "read");
        var verdict2 = matrix.Evaluate(identity, "resource:database", "read");
        var verdict3 = matrix.Evaluate(identity, "resource:anything", "read");

        // Assert
        verdict1.Allowed.Should().BeTrue();
        verdict2.Allowed.Should().BeTrue();
        verdict3.Allowed.Should().BeTrue();
    }

    [Fact]
    public void SystemIdentityFactory_CreatesCorrectIdentity()
    {
        // Act
        var identity = CommandIdentity.System("kernel", "instance1");

        // Assert
        identity.ActorId.Should().Be("svc:kernel");
        identity.ActorType.Should().Be(ActorType.SystemService);
        identity.OnBehalfOfPrincipalId.Should().Be("system:kernel");
        identity.PrincipalType.Should().Be(PrincipalType.System);
        identity.TenantId.Should().Be("system");
        identity.InstanceId.Should().Be("instance1");
        identity.AuthenticationMethod.Should().Be("Internal");
        identity.EffectivePrincipalId.Should().Be("system:kernel");
    }

    [Fact]
    public void UserIdentityFactory_CreatesCorrectIdentity()
    {
        // Act
        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "admin", "user" },
            groupIds: new[] { "group:eng" },
            authMethod: "OAuth2",
            sessionId: "session123");

        // Assert
        identity.ActorId.Should().Be("user:alice");
        identity.ActorType.Should().Be(ActorType.Human);
        identity.OnBehalfOfPrincipalId.Should().Be("user:alice");
        identity.PrincipalType.Should().Be(PrincipalType.User);
        identity.TenantId.Should().Be("tenant1");
        identity.InstanceId.Should().Be("instance1");
        identity.GroupIds.Should().BeEquivalentTo(new[] { "group:eng" });
        identity.Roles.Should().BeEquivalentTo(new[] { "admin", "user" });
        identity.AuthenticationMethod.Should().Be("OAuth2");
        identity.SessionId.Should().Be("session123");
        identity.EffectivePrincipalId.Should().Be("user:alice");
    }

    [Fact]
    public void AiAgentIdentityFactory_PreservesOnBehalfOfPrincipalId()
    {
        // Arrange
        var userIdentity = CommandIdentity.ForUser(
            userId: "bob",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" },
            groupIds: new[] { "group:sales" });

        // Act
        var aiIdentity = CommandIdentity.ForAiAgent("gemini-user", userIdentity);

        // Assert
        aiIdentity.ActorId.Should().Be("ai:gemini-user");
        aiIdentity.ActorType.Should().Be(ActorType.AiAgent);
        aiIdentity.OnBehalfOfPrincipalId.Should().Be("user:bob");
        aiIdentity.PrincipalType.Should().Be(PrincipalType.User);
        aiIdentity.TenantId.Should().Be("tenant1");
        aiIdentity.InstanceId.Should().Be("instance1");
        aiIdentity.GroupIds.Should().BeEquivalentTo(new[] { "group:sales" });
        aiIdentity.Roles.Should().BeEquivalentTo(new[] { "user" });
        aiIdentity.EffectivePrincipalId.Should().Be("user:bob");
        aiIdentity.DelegationChain.Should().Contain("ai:gemini-user");
    }

    [Fact]
    public void DelegationChainAppend_WithDelegation_AppendsCorrectly()
    {
        // Arrange
        var userIdentity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" });

        // Act
        var geminiIdentity = userIdentity.WithDelegation("ai:gemini-user");
        var claudeIdentity = geminiIdentity.WithDelegation("ai:claude-system");

        // Assert
        userIdentity.DelegationChain.Should().BeEmpty();
        geminiIdentity.DelegationChain.Should().BeEquivalentTo(new[] { "ai:gemini-user" });
        claudeIdentity.DelegationChain.Should().BeEquivalentTo(new[] { "ai:gemini-user", "ai:claude-system" });

        // OnBehalfOfPrincipalId should remain unchanged
        userIdentity.OnBehalfOfPrincipalId.Should().Be("user:alice");
        geminiIdentity.OnBehalfOfPrincipalId.Should().Be("user:alice");
        claudeIdentity.OnBehalfOfPrincipalId.Should().Be("user:alice");
    }

    [Fact]
    public void MaintenanceModeDeniesUsers_SystemLevelDeny_BlocksAllUserOperations()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        // System-level deny (maintenance mode)
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "maintenance-mode",
            Level = HierarchyLevel.System,
            ScopeId = "system",
            Resource = "*",
            Action = "*",
            Decision = LevelDecision.Deny,
            Description = "System in maintenance mode"
        });

        // Even admin user should be denied
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "admin-allow",
            Level = HierarchyLevel.User,
            ScopeId = "user:admin",
            Resource = "*",
            Action = "*",
            Decision = LevelDecision.Allow
        });

        var adminIdentity = CommandIdentity.ForUser(
            userId: "admin",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "admin" });

        var userIdentity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" });

        // Act
        var adminVerdict = matrix.Evaluate(adminIdentity, "resource:file", "read");
        var userVerdict = matrix.Evaluate(userIdentity, "resource:file", "read");

        // Assert
        adminVerdict.Allowed.Should().BeFalse();
        adminVerdict.DecidedAtLevel.Should().Be(HierarchyLevel.System);
        adminVerdict.RuleId.Should().Be("maintenance-mode");

        userVerdict.Allowed.Should().BeFalse();
        userVerdict.DecidedAtLevel.Should().Be(HierarchyLevel.System);
        userVerdict.RuleId.Should().Be("maintenance-mode");
    }

    [Fact]
    public void InstanceLevelDeny_OverridesGroupAndUserAllows()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        // Instance deny (production locked)
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "instance-locked",
            Level = HierarchyLevel.Instance,
            ScopeId = "instance1",
            Resource = "resource:database",
            Action = "write",
            Decision = LevelDecision.Deny,
            Description = "Production instance locked for deployments"
        });

        // User allow (should be ignored)
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "user-allow",
            Level = HierarchyLevel.User,
            ScopeId = "user:alice",
            Resource = "resource:database",
            Action = "write",
            Decision = LevelDecision.Allow
        });

        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "admin" });

        // Act
        var verdict = matrix.Evaluate(identity, "resource:database", "write");

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.DecidedAtLevel.Should().Be(HierarchyLevel.Instance);
        verdict.RuleId.Should().Be("instance-locked");
        verdict.Reason.Should().Contain("Production instance locked");
    }

    [Fact]
    public void ResourcePrefixMatch_WildcardSuffix_MatchesCorrectly()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "file-prefix-allow",
            Level = HierarchyLevel.User,
            ScopeId = "user:alice",
            Resource = "resource:file:*", // Prefix match
            Action = "read",
            Decision = LevelDecision.Allow
        });

        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" });

        // Act
        var verdict1 = matrix.Evaluate(identity, "resource:file:doc1", "read");
        var verdict2 = matrix.Evaluate(identity, "resource:file:doc2", "read");
        var verdict3 = matrix.Evaluate(identity, "resource:database:db1", "read");

        // Assert
        verdict1.Allowed.Should().BeTrue();
        verdict2.Allowed.Should().BeTrue();
        verdict3.Allowed.Should().BeFalse(); // Does not match prefix
    }

    [Fact]
    public void WildcardAction_MatchesAllActions()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "admin-all-actions",
            Level = HierarchyLevel.User,
            ScopeId = "user:admin",
            Resource = "resource:file",
            Action = "*", // All actions
            Decision = LevelDecision.Allow
        });

        var identity = CommandIdentity.ForUser(
            userId: "admin",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "admin" });

        // Act
        var verdict1 = matrix.Evaluate(identity, "resource:file", "read");
        var verdict2 = matrix.Evaluate(identity, "resource:file", "write");
        var verdict3 = matrix.Evaluate(identity, "resource:file", "delete");

        // Assert
        verdict1.Allowed.Should().BeTrue();
        verdict2.Allowed.Should().BeTrue();
        verdict3.Allowed.Should().BeTrue();
    }

    [Fact]
    public void MultipleGroupMembership_DenyFromAnyGroup_ReturnsDeny()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        // Group1 allows
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "group1-allow",
            Level = HierarchyLevel.UserGroup,
            ScopeId = "group:eng",
            Resource = "resource:file",
            Action = "write",
            Decision = LevelDecision.Allow
        });

        // Group2 denies
        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "group2-deny",
            Level = HierarchyLevel.UserGroup,
            ScopeId = "group:readonly",
            Resource = "resource:file",
            Action = "write",
            Decision = LevelDecision.Deny
        });

        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" },
            groupIds: new[] { "group:eng", "group:readonly" });

        // Act
        var verdict = matrix.Evaluate(identity, "resource:file", "write");

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.DecidedAtLevel.Should().Be(HierarchyLevel.UserGroup);
        verdict.RuleId.Should().Be("group2-deny");
    }

    [Fact]
    public void InactiveRule_IsIgnored()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "inactive-allow",
            Level = HierarchyLevel.User,
            ScopeId = "user:alice",
            Resource = "resource:file",
            Action = "read",
            Decision = LevelDecision.Allow,
            IsActive = false // Inactive rule
        });

        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" });

        // Act
        var verdict = matrix.Evaluate(identity, "resource:file", "read");

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.RuleId.Should().Be("default-deny");
    }

    [Fact]
    public void LevelResults_CapturesFullHierarchyEvaluation()
    {
        // Arrange
        var matrix = new AccessVerificationMatrix();

        matrix.AddRule(new HierarchyAccessRule
        {
            RuleId = "user-allow",
            Level = HierarchyLevel.User,
            ScopeId = "user:alice",
            Resource = "resource:file",
            Action = "read",
            Decision = LevelDecision.Allow
        });

        var identity = CommandIdentity.ForUser(
            userId: "alice",
            tenantId: "tenant1",
            instanceId: "instance1",
            roles: new[] { "user" });

        // Act
        var verdict = matrix.Evaluate(identity, "resource:file", "read");

        // Assert
        verdict.LevelResults.Should().HaveCount(5); // System, Tenant, Instance, UserGroup, User
        verdict.LevelResults.Should().Contain(r => r.Level == HierarchyLevel.System && r.Decision == LevelDecision.NoRule);
        verdict.LevelResults.Should().Contain(r => r.Level == HierarchyLevel.Tenant && r.Decision == LevelDecision.NoRule);
        verdict.LevelResults.Should().Contain(r => r.Level == HierarchyLevel.Instance && r.Decision == LevelDecision.NoRule);
        verdict.LevelResults.Should().Contain(r => r.Level == HierarchyLevel.UserGroup && r.Decision == LevelDecision.NoRule);
        verdict.LevelResults.Should().Contain(r => r.Level == HierarchyLevel.User && r.Decision == LevelDecision.Allow);
    }
}
