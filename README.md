# LiveDesk

움직이는 바탕화면 + 바탕화면 에디터.

```
livedesk/
  app/index.html     렌더러 + 에디터 (단일 파일, 의존성 없음, Canvas 2D)
  host/              Windows 호스트 (C# / .NET 8 / WebView2) — 바탕화면 아이콘 뒤에 렌더러를 붙임
```

## 에디터만 바로 써 보기

`app/index.html`을 브라우저(Chrome/Edge)로 열면 됩니다. `app/index.html?mode=wallpaper`는 배경화면 전용 화면입니다.

- 이미지: 업로드 버튼 / 캔버스에 드래그 / Ctrl+V 붙여넣기
- 배치: 드래그로 이동, 모서리 핸들로 크기(Shift = 비율 해제), 위쪽 핸들로 회전(Shift = 15° 단위)
- 움직임: 둥실둥실, 흔들흔들, 회전, 맥동, 깜빡임, 원 궤도, 흘러가기, 떨림, 마우스 따라 움직임 — 여러 개 중첩 가능
- 프리셋: 디지털/아날로그 시계, 날짜, 텍스트, 파티클 7종(눈·비·반딧불·별·꽃잎·방울·불씨), 물결
- 단축키: Ctrl+Z / Ctrl+Y, Ctrl+D 복제, Delete 삭제, 방향키 이동(Shift = 10px), Esc 선택 해제
- 내보내기/불러오기: 이미지까지 포함한 `.livedesk.json` 한 파일

## 실제 바탕화면에 적용 (Windows 10/11)

[.NET 8 SDK](https://dotnet.microsoft.com/download)가 필요합니다. WebView2 런타임은 Windows 11과 최신 Windows 10에 기본 포함되어 있습니다.

```bash
cd host
dotnet run -c Release
```

배포용 exe:

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o ../dist
```

실행하면 트레이 아이콘이 생깁니다. 더블클릭 → 에디터, 에디터에서 **바탕화면에 적용**을 누르면 즉시 반영됩니다.
트레이 메뉴: 에디터 열기 / 일시정지 / Windows 시작 시 자동 실행 / 종료.

## 부하를 줄이는 방식

- 움직이는 요소가 있을 때만 FPS 제한(기본 30) 루프를 돌리고, 시계만 있으면 1초에 한 번, 완전히 정적이면 변경 시에만 그립니다.
- 전체화면·최대화 창이 덮은 모니터의 배경화면은 그리기를 멈춥니다(호스트가 감지).
- 파티클은 한 번의 path/스프라이트로 묶어 그리며, 품질(해상도 배율)과 FPS를 장면마다 조절할 수 있습니다.
- 4096px을 넘는 이미지는 업로드 시 축소합니다.
