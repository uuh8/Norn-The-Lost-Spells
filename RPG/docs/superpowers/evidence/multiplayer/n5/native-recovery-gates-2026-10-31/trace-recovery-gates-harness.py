from pathlib import Path
p=Path(__file__).parent/'Run-N5RecoveryGatesNative.ps1';s=p.read_text(encoding='utf-8-sig')
a="if($Case -eq 'gates-2'){$config.FirstOffset=@{x=-5;y=1;z=-3};$config.SecondOffset=@{x=-1;y=1;z=-3}}"
assert s.count(a)==1;s=s.replace(a,a.replace("$Case -eq 'gates-2'","$Case -ne 'gates-1'"))
a="catch{if($_.Exception.Message -like 'FAIL:*'){throw}};if([DateTime]::UtcNow -gt $end)"
b="catch{Save-Json (Join-Path $ce 'wait-last-error.json') @{Label=$label;Exception=$_.Exception.Message;Type=$_.Exception.GetType().FullName;RecordedUtc=[DateTime]::UtcNow.ToString('O')};if($_.Exception.Message -like 'FAIL:*'){throw}};if([DateTime]::UtcNow -gt $end)"
assert s.count(a)==1;s=s.replace(a,b)
a="Wait-State 'ActualHistoryRingCovered' {$seed.State -eq 'HistoryComplete' -and $seed.HistoryStep -eq 80 -and $ds.MaterialHistoryCapacity -eq 128 -and ($ds.MaterialHistoryRecords-$historyStart) -ge 160 -and (Chunk-Text $ds) -eq (Chunk-Text $b) -and (Exact-Acks $b 1) -and (Chunk-Text $a) -eq (Chunk-Text $initialA) -and (Cells-Text $sa) -eq (Cells-Text $initialStore)} 35"
b="""Wait-State 'ActualHistoryRingCovered' {
 $checks=@{SeedComplete=($seed.State -eq 'HistoryComplete');Rounds80=($seed.HistoryStep -eq 80);Capacity128=($ds.MaterialHistoryCapacity -eq 128);
  History160=(($ds.MaterialHistoryRecords-$historyStart) -ge 160);BetaSignature=((Chunk-Text $ds) -eq (Chunk-Text $b));BetaAck=(Exact-Acks $b 1);
  AlphaSignature=((Chunk-Text $a) -eq (Chunk-Text $initialA));AlphaCells=((Cells-Text $sa) -eq (Cells-Text $initialStore))}
 Save-Json (Join-Path $ce 'history-conditions-last.json') @{Checks=$checks;HistoryStart=$historyStart;HistoryNow=$ds.MaterialHistoryRecords;RecordedUtc=[DateTime]::UtcNow.ToString('O')}
 # 与原八个AND完全相同，只把逐项值留档；不降低记录数/数量/页面/一致性门槛。
 $checks.Values -notcontains $false
 } 35"""
assert s.count(a)==1;s=s.replace(a,b)
p.write_text(s,encoding='utf-8-sig',newline='\n')
print('Added bounded last-error and exact eight-condition tracing; same thresholds and supported case-2 positions for case-3.')
