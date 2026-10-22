# Norn Monorepo

This repository contains sibling RPG/ (Unity), Server/ (ASP.NET Core), and Shared/ (pure contracts).
Read RPG/AGENTS.md for project workflow, architecture, comments, and validation requirements.
Git operations belong to this outer root; do not initialize nested repositories in the components.
Keep Unity Assets/meta and the sibling Shared relative reference intact. Do not commit local secrets, SDKs, database data, or build caches.
The user permits SubAgents as needed; coordinate shared Editor/test/build/Git operations centrally.
