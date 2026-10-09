from pathlib import Path

here = Path(__file__).resolve().parent
source = here.parent / "NpcFollow"
for name in ("Stub.cs.txt", "Directory.Build.props", ".gitignore"):
    target = here / name
    if not target.exists():
        target.write_bytes((source / name).read_bytes())
project = here / "OptionalBranchFollowRegression.csproj"
if not project.exists():
    project.write_bytes((source / "NpcFollowRegression.csproj").read_bytes())
print("Independent optional-branch harness prepared")
