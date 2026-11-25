# GitHubPackagePublisher

## Installation

Download the Unity package from the [Releases](../../releases) page and import it using `Assets > Import Package > Custom Package...`.

When imported into a Git repository, the editor tool adds its own `Assets/GitHubPackagePublisher` folder and folder metadata to that repository's local `.git/info/exclude`. This keeps the publisher tool from appearing as project changes without modifying the repository's shared `.gitignore`. The rule is added only if it is not already present.
