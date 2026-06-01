# 🧬 GitLoom: Technical Roadmap & Architecture Blueprint

GitLoom is a premium, offline-first, cross-platform desktop **Git GUI** built natively in C# and Avalonia UI. It serves as a beautiful, high-performance, and entirely free alternative to commercial clients like GitKraken, powered by the optimized C-based `libgit2` engine.

---

## 1. Core Vision & Architectural Goals

- **Zero Cloud Friction:** 100% offline-first, no accounts, no telemetry, and no developer API keys. It runs entirely on the local file system.
- **Visual Superiority:** Outperform traditional GUIs with a glowing, modern glassmorphic theme, micro-animations, and high-fidelity branch vector graphs.
- **Double-Layer Optimization:**
  - **Native Layer:** Use `LibGit2Sharp` (compiled C-bindings) for near-instantaneous indexing, commits, and diff parsing.
  - **Caching Layer:** Use a local SQLite database to cache repository metadata, categories, recent commits, and settings so workspaces load in under 100ms.

---

## 2. Technical Stack & Dependencies

- **Desktop Framework:** Avalonia UI (v11.1.3 - Stable)
- **MVVM Engine:** `CommunityToolkit.Mvvm` (v8.4.2)
- **Git Engine:** `LibGit2Sharp` (v0.30.0+ - standard native libgit2 bindings)
- **Local Cache:** SQLite via Entity Framework Core (`Microsoft.EntityFrameworkCore.Sqlite`)
- **Data Visualization:** `LiveChartsCore.SkiaSharpView.Avalonia` (v2.0.4)
- **Vector Rendering:** Custom Avalonia `DrawingContext` and canvas vector paths for the commit graph lines.

---

## 3. Recommended Project Structure

```text
GitLoom/
├── GitLoom.sln
├── GitLoom.Core/                       # Domain logic, Git engine, SQLite cache
│   ├── GitLoom.Core.csproj
│   ├── GitService.cs                     # Core LibGit2Sharp wrappers
│   ├── Models/
│   │   ├── Repository.cs                 # Bookmarked repositories
│   │   ├── WorkspaceCategory.cs          # Custom folders/groups for projects
│   │   └── AppSetting.cs                 # User configurations (theme, credentials)
│   ├── AppDbContext.cs                   # SQLite Entity Framework DbContext
│   └── Analytics/
│       └── RepositoryAnalyzer.cs         # Parses punchcards and language stats
│
├── GitLoom.App/                        # Avalonia UI desktop application
│   ├── GitLoom.App.csproj
│   ├── App.axaml                         # Global styles, fonts, and assets
│   ├── ViewLocator.cs
│   ├── ViewModels/
│   │   ├── ViewModelBase.cs
│   │   ├── MainWindowViewModel.cs        # Orchestrates workspace navigation
│   │   ├── RepoDashboardViewModel.cs     # Commits, diffs, and staging
│   │   └── AnalyticsViewModel.cs         # Churn and language breakdowns
│   └── Views/
│       ├── MainWindow.axaml              # Sidebar navigation and workspace tabs
│       ├── RepoDashboardView.axaml       # Commit timeline, Staging lists
│       ├── DiffViewerControl.axaml       # Side-by-side green/red code diffs
│       └── CommitGraphCanvas.cs          # Custom SkiaSharp canvas for branch lines
│
└── GitLoom.Tests/                      # xUnit testing suite
    ├── GitLoom.Tests.csproj
    ├── GitServiceTests.cs
    └── AnalyticsTests.cs
```

---

## 4. SQLite Cache Database Schema

To ensure rapid load times and secure credentials, GitLoom caches bookmarked directories, category groupings, and GitHub user tokens.

```mermaid
erDiagram
    WorkspaceCategory ||--o{ Repository : contains
    Repository ||--o{ RepoSetting : configures
    GitHubProfile ||--o{ CloudRepository : syncs
    
    WorkspaceCategory {
        int CategoryId PK
        string Name
        int DisplayOrder
    }
    
    Repository {
        int RepositoryId PK
        string Path
        string DisplayName
        string LastAccessed
        int CategoryId FK
    }
    
    GitHubProfile {
        int ProfileId PK
        string Username
        string AvatarUrl
        string EncryptedOAuthToken
    }
    
    CloudRepository {
        int CloudRepoId PK
        string FullName
        string CloneUrl
        bool IsPrivate
        int ProfileId FK
    }
    
    RepoSetting {
        string Key PK
        string Value
    }
    
    AppSetting {
        string Key PK
        string Value
    }
```

---

## 5. Phase-by-Phase Implementation Plan

### 🚀 Phase 1: Scaffolding & Workspace Manager
- **Core Goals:** Set up projects, install NuGet libraries, configure the SQLite local cache, and design the initial folder browser.
- **Key Actions:**
  - Create the `GitLoom.Core`, `GitLoom.App`, and `GitLoom.Tests` assemblies.
  - Implement `AppDbContext` and migrations to support workspaces and bookmarked repository folders.
  - Build the glassmorphic sidebar panel listing repository categories.
  - Integrate a directory browser to let users add existing `.git` folders to the app.

### 🧬 Phase 2: High-Performance Commit History & Graph
- **Core Goals:** Query repositories using `LibGit2Sharp` and render the vertically scrolling commit timeline with glowing, color-coded branch connection lines.
- **Key Actions:**
  - Write `GitService.cs` to query commits, hashes, authors, and dates from `libgit2`.
  - Design the `RepoDashboardView` displaying the scrollable history card stream.
  - Create `CommitGraphCanvas.cs`, a custom Avalonia control that uses `DrawingContext` vector math to draw lines tracking active branches, splits, and merges (glowing cyan, magenta, and emerald paths).

### 🛠️ Phase 3: Staging, Diffs, & Committing
- **Core Goals:** Parse index modifications, render side-by-side code diffs, stage files, and author commits.
- **Key Actions:**
  - Implement staging status checks (Modified, Untracked, Staged, Deleted).
  - Create the custom `DiffViewerControl` displaying added (green) and removed (red) lines side-by-side or unified.
  - Implement `StageFile` and `UnstageFile` commands in the `GitService`.
  - Create a highly polished commit message pane supporting quick emoji shortcuts (e.g. `:bug:`, `:sparkles:`).

### 🌿 Phase 4: Branch & Remote Management
- **Core Goals:** Branch tree navigation, checkouts, branch creation, stash integration, and push/pull commit counts.
- **Key Actions:**
  - Build the left pane branch tree showing local and remote tracking heads.
  - Implement `CheckoutBranch` with safety checks for unstaged file overwrites.
  - Implement branch creation, deletion, and basic stashing operations.
  - Query upstream remotes to display `Ahead` (outgoing) and `Behind` (incoming) commit counters.

### 📊 Phase 5: Repository Analytics & Churn (Premium Polish)
- **Core Goals:** Add elite analytics widgets to make GitLoom feel premium, modern, and engaging.
- **Key Actions:**
  - Implement `RepositoryAnalyzer` to group commit data:
    - **Activity Punch Card:** Hours and days of high developer activity.
    - **Code Churn:** Net lines added vs. deleted over time.
    - **Language Composition:** SkiaSharp donut chart mapping codebase file ratios.
  - Add smooth transitions and micro-animations to tab switching.

### ☁️ Phase 6: GitHub OAuth Integration & Cloud Cloner
- **Core Goals:** Secure GitHub device login, list public/private remote repos, and automate local cloning.
- **Key Actions:**
  - Build a custom **OAuth 2.0 Device Flow Client** (`https://github.com/login/device/code`) allowing direct, free secure browser logins.
  - Securely encrypt the token locally using Windows Data Protection API (DPAPI) and save it in SQLite (`GitHubProfile`).
  - Implement `GitHubApiService.cs` to consume the GitHub REST API (`/user/repos`) to list all remote repositories.
  - Build the "GitHub Workspace Cloner" UI panel to clone repos directly over HTTPS using `LibGit2Sharp.Repository.Clone()` and register them to local workspaces in 1 click.

---

## 6. Premium Design Token Specifications

To ensure the app looks premium and futuristic, the styling will strictly adhere to the following color palette and glassmorphism settings:

| Token Key | HEX / HSL Value | Purpose |
| :--- | :--- | :--- |
| `BgObsidian` | `#0C0F12` | Solid background, deep base |
| `PanelGlass` | `rgba(20, 25, 31, 0.85)` | Blur panels, primary widgets |
| `BorderGlass` | `rgba(255, 255, 255, 0.12)` | Clean 1.5px glowing borders |
| `TextWhite` | `#FFFFFF` | Primary titles, bold text |
| `TextMuted` | `#A6ADC8` | secondary details, dates, author names |
| `BranchCyan` | `#89B4FA` / HSL Blue | Cyan path for `main` or active branch |
| `BranchPink` | `#F5C2E7` / HSL Pink | Pink path for feature branches |
| `BranchGreen` | `#A6E3A1` / HSL Green | Staged badges, green diff additions |
| `BranchRed` | `#F38BA8` / HSL Red | Deleted files, red diff deletions |
| `AcrylicBlur` | `BackgroundSource=Digger` | Windows/macOS native acrylic backing |

---

## 7. Next Steps & Active Checklist

- [ ] **Step 1:** Create new solution folder, initialize C# projects (`Core`, `App`, `Tests`).
- [ ] **Step 2:** Reference package dependencies (Avalonia, LibGit2Sharp, EF Core SQLite, MVVM, LiveCharts2).
- [ ] **Step 3:** Implement SQLite database setup and folder bookmarks cache models.
- [ ] **Step 4:** Build the Repository Browser Sidebar shell.
