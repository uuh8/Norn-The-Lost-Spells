"""离线使用当前工程真实 Unity References 编译；不启动 Editor，不等价于 Unity Import/Play。"""
import pathlib
import subprocess
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[4]
HERE = pathlib.Path(__file__).resolve().parent
BIN = HERE / 'compiled'
BIN.mkdir(exist_ok=True)
NS = {'m': 'http://schemas.microsoft.com/developer/msbuild/2003'}
CSC = pathlib.Path('C:/Program Files/dotnet/sdk/8.0.302/Roslyn/bincore/csc.dll')
EXTRA = {
    'Game.ElementField': ['Assets/_Project/Scripts/ElementField/Gases/GasDiluteDissipation.cs'],
    'Game.Rendering.Tests': ['Assets/_Project/Tests/Rendering/FallingLiquidPresentationTests.cs'],
}
results = []
for name in ['Game.ElementField', 'Game.Rendering', 'Game.ElementField.Tests', 'Game.Rendering.Tests', 'Game.PbfTask13.Editor']:
    project = ET.parse(ROOT / (name + '.csproj')).getroot()
    definitions = project.find('.//m:DefineConstants', NS).text
    args = ['/nologo', '/nostdlib+', '/target:library', '/langversion:9.0', '/unsafe+', '/nowarn:0169,0649',
            '/define:' + definitions, '/out:"' + str(BIN / (name + '.dll')) + '"']
    refs = set()
    for node in project.findall('.//m:Reference', NS):
        hint = node.find('m:HintPath', NS)
        if hint is not None:
            path = pathlib.Path(hint.text)
            if not path.is_absolute():
                path = ROOT / path
            if not path.is_file():
                raise FileNotFoundError(path)
            refs.add(str(path.resolve()))
    for node in project.findall('.//m:ProjectReference', NS):
        dependency = pathlib.Path(node.attrib['Include']).stem
        path = BIN / (dependency + '.dll')
        if not path.is_file():
            path = ROOT / 'Library/ScriptAssemblies' / (dependency + '.dll')
        if not path.is_file():
            raise FileNotFoundError(path)
        refs.add(str(path.resolve()))
    args += ['/reference:"' + path + '"' for path in sorted(refs)]
    sources = {str((ROOT / node.attrib['Include']).resolve()) for node in project.findall('.//m:Compile', NS)}
    sources.update(str((ROOT / source).resolve()) for source in EXTRA.get(name, []))
    args += ['"' + source + '"' for source in sorted(sources)]
    response = BIN / (name + '.rsp')
    response.write_text('\n'.join(args), encoding='utf-8-sig')
    result = subprocess.run(['dotnet', str(CSC), '/noconfig', '@' + str(response)], cwd=ROOT, text=True, encoding='utf-8', errors='replace', capture_output=True)
    report = result.stdout + result.stderr
    (HERE / (name + '-compile.txt')).write_text(report, encoding='utf-8')
    print(name, 'ExitCode=' + str(result.returncode), 'Sources=' + str(len(sources)), 'References=' + str(len(refs)))
    if result.returncode:
        print(report[:12000])
        raise SystemExit(result.returncode)
    results.append(name + ': PASS (Roslyn against installed Unity references; no Editor/Shader/Play evidence)')
(HERE / 'compile-summary.txt').write_text('\n'.join(results) + '\n', encoding='utf-8')
