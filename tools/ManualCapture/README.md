# 설명서 화면 캡처 (ManualCapture)

[사용 설명서](../../docs/manual/README.md)의 스크린샷과 GIF를 실제 프로그램 화면으로 자동 생성한다.
화면(XAML)이 바뀌는 업데이트 뒤에 다시 돌리면 설명서 그림이 최신 화면으로 바뀐다.

## 다시 찍기

1. GitHub **Actions › Manual Capture › Run workflow** (main 기준으로 실행)
2. 끝나면 실행 결과 아래 **Artifacts › manual-images** 를 받는다.
3. 압축을 풀어 `docs/manual/images/`에 덮어쓰고 커밋한다.

`tools/ManualCapture/` 아래 파일을 고쳐 push해도 자동으로 실행된다.

## 구성

| 파일 | 하는 일 |
|---|---|
| `Program.cs` | Windows 러너에서 앱을 띄우고 UI Automation · 마우스 · 키보드로 `samples/` 맵을 조작하며 스크린샷과 동영상 프레임을 남긴다. 단계마다 실패해도 계속 진행하고 `_fail_*.png`를 남긴다 (`capture-raw` 아티팩트의 `log.txt` 참고). |
| `make_assets.py` | 스크린샷에 번호 · 강조 상자를 그리고 잘라내며, 프레임에 커서를 그려 GIF로 만든다. |
| `../../.github/workflows/manual-capture.yml` | 빌드 → 캡처 → 후처리 → 아티팩트 업로드 |

- 버튼은 이름(글자)이나 `x:Name`으로 찾는다. 버튼 글자 · `x:Name`을 바꾸면 `Program.cs`의 해당 단계도 바꾼다.
- 맵 위 좌표는 커서를 올렸을 때 상태 표시줄에 나오는 픽셀 좌표로 보정하므로 창 크기 · 배율이 달라도 맞는다.
- 새 화면을 추가하려면 `Program.cs`의 `Scenario()`에 `Step(...)`을 넣고, 필요하면 `make_assets.py`의 `SPEC`에 번호 · 자르기를 정한다.
