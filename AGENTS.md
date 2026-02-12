# SimplyShare - 개발 에이전트 정책 (Agents Policy)

> spec.md 기반으로 올바른 애플리케이션을 만들기 위한 개발 정책 및 가이드
> 버전: 1.0 | 작성일: 2026-02-11

---

## 1. 개발 원칙

### 1.1 기술 원칙
- **반드시 .NET 10 / C# 14** 최신 문법을 사용한다.
- **Native AOT 호환성**을 모든 코드에서 최우선으로 고려한다.
  - `System.Text.Json` Source Generator 사용 (리플렉션 기반 직렬화 금지)
  - `dynamic`, `Reflection.Emit`, `Assembly.Load` 등 동적 코드 금지
  - 트리밍 안전한 패턴만 사용
- **MVVM 패턴**을 엄격히 준수한다 (CommunityToolkit.Mvvm 사용 권장).
- 핫 패스에서는 `Span<T>`, `stackalloc`, `ArrayPool<T>`를 우선 사용한다.
- 모든 비동기 작업은 `async/await` + `CancellationToken`을 반드시 지원한다.

### 1.2 코드 품질 원칙
- Nullable Reference Types 전역 활성화 (`<Nullable>enable</Nullable>`).
- 파일 스코프 네임스페이스, primary constructor 등 최신 C# 14 문법 적극 사용.
- 매직 넘버 금지 — 상수 또는 설정값으로 정의.
- 모든 public API에 XML 문서 주석 작성.

---

## 2. 구현 순서 (Phase)

에이전트는 다음 순서로 기능을 구현한다. 각 Phase 완료 후 **컴파일 확인** 필수.

### Phase 1: 프로젝트 초기화
1. WPF 프로젝트 생성 (`.csproj` 설정 — Native AOT, .NET 10)
2. 폴더 구조 생성 (`spec.md` 6절 참조)
3. NuGet 패키지 설정
4. 기본 App.xaml / MainWindow.xaml 스캐폴딩
5. **검증**: `dotnet build` 성공

### Phase 2: 핵심 인프라
1. **데이터 모델** 정의 (`Models/`)
   - `DeviceInfo`: 장치 정보 (닉네임, IP, 포트, 상태)
   - `TransferRequest`: 전송 요청 정보
   - `TransferProgress`: 전송 진행률
   - `AppSettings`: 앱 설정 (닉네임, 네트워크 대역, 저장 경로 등)
2. **JSON 직렬화 컨텍스트** (Source Generator)
   - `[JsonSerializable]` 속성으로 모든 모델 등록
3. **설정 관리 서비스** (`Services/SettingsService.cs`)
   - JSON 파일 기반 설정 저장/로드
4. **검증**: 모델 직렬화/역직렬화 테스트

### Phase 3: 장치 발견 (Discovery)
1. **UDP 브로드캐스트 송수신** (`Core/Discovery/`)
   - `DiscoveryService`: 장치 발견 메인 서비스
   - Discovery 메시지 발송 (주기적)
   - Heartbeat 송수신
   - Goodbye 메시지 (앱 종료 시)
2. **네트워크 대역 필터링**
   - `192.168.100.*` 패턴 파싱 및 IP 매칭
3. **장치 목록 관리**
   - 온라인/오프라인 상태 추적
   - Heartbeat 타임아웃 감지
4. **검증**: 두 인스턴스 실행하여 상호 발견 확인

### Phase 4: 암호화 (Crypto)
1. **ECDH 키 교환** (`Core/Crypto/`)
   - `KeyExchangeService`: ECDH P-256 키 쌍 생성/교환
   - 공유 비밀(Shared Secret)에서 AES 키 유도 (HKDF)
2. **AES-256-GCM 암/복호화**
   - `CryptoService`: 스트리밍 암호화/복호화
   - 파일 전송 시 청크 단위 암호화
3. **검증**: 키 교환 → 암호화 → 복호화 라운드트립 테스트

### Phase 5: 데이터 전송 (Transfer)
1. **TCP 서버/클라이언트** (`Core/Transfer/`)
   - `TransferServer`: TCP 리스너 (수신 대기)
   - `TransferClient`: TCP 연결 (전송)
2. **전송 프로토콜 구현**
   - 키 교환 핸드셰이크
   - 전송 요청/수락/거부
   - 텍스트 전송
   - 파일 전송 (청크 단위 스트리밍, 진행률 콜백)
   - 복수 파일/폴더 전송
3. **전송 관리**
   - 동시 다중 전송 지원
   - 연결 끊김 감지 및 정리
   - 전송 취소 지원
4. **검증**: 텍스트 전송 → 소형 파일 전송 → 대용량 파일 전송 순차 테스트

### Phase 6: 클립보드 감시
1. **클립보드 모니터** (`Core/Clipboard/`)
   - Windows 클립보드 변경 감지 (AddClipboardFormatListener)
   - 텍스트 콘텐츠 자동 인식
2. **검증**: 클립보드 복사 → 자동 감지 확인

### Phase 7: UI 구현
1. **MVVM ViewModel** (`ViewModels/`)
   - `MainViewModel`: 메인 화면 로직
   - `SettingsViewModel`: 설정 화면 로직
   - `SetupViewModel`: 최초 설정 로직
   - `TransferDialogViewModel`: 수신 수락/거부
2. **Views (XAML)**
   - `MainWindow.xaml`: 장치 목록 + 드롭 영역 + 클립보드 패널 + 진행률
   - `SettingsView.xaml`: 설정 화면
   - `SetupView.xaml`: 최초 실행 닉네임 설정
   - `TransferDialog.xaml`: 수신 수락/거부 다이얼로그
3. **드래그 앤 드롭** 구현
   - 파일/폴더 드래그 시 시각적 피드백
   - 드롭 후 선택된 대상에게 전송
4. **시스템 트레이** 구현
   - NotifyIcon 연동
   - 최소화 시 트레이 이동
   - 우클릭 컨텍스트 메뉴
5. **Windows Toast 알림** 구현
6. **검증**: 전체 UI 플로우 수동 테스트

### Phase 8: 통합 및 마무리
1. 전체 기능 통합 테스트
2. Native AOT 빌드 확인 (`dotnet publish -c Release`)
3. 예외 처리 및 에러 메시지 정리
4. README.md 작성

---

## 3. 코딩 규칙

### 3.1 네이밍
| 대상 | 규칙 | 예시 |
|------|------|------|
| 클래스/레코드 | PascalCase | `DeviceInfo`, `TransferService` |
| 인터페이스 | I + PascalCase | `IDiscoveryService` |
| 메서드 | PascalCase | `SendTextAsync()` |
| 지역 변수 | camelCase | `deviceList`, `buffer` |
| 상수 | PascalCase | `DefaultPort`, `MaxBufferSize` |
| private 필드 | _camelCase | `_discoveryService` |
| async 메서드 | Async 접미사 | `SendFileAsync()` |

### 3.2 파일 구성
- 하나의 파일에 하나의 주요 타입 (record/class)
- 파일명 = 타입명 (`DeviceInfo.cs` → `DeviceInfo` record)
- XAML과 코드비하인드는 같은 폴더에 배치

### 3.3 의존성 주입
- 서비스 간 Dependencies는 생성자 주입 사용
- 서비스는 인터페이스 기반 (`IDiscoveryService`, `ITransferService` 등)
- App.xaml.cs에서 서비스 등록 (SimpleIoC 또는 Microsoft.Extensions.DependencyInjection)

---

## 4. 배포 체크리스트 (ReadyToRun + 단일 파일)

구현 중 다음을 반드시 확인한다:

- [ ] `System.Text.Json` → `JsonSerializerContext` Source Generator 사용 (리플렉션 최소화)
- [ ] `PublishSingleFile`, `PublishReadyToRun`, `SelfContained` 활성화
- [ ] `dotnet publish -c Release -r win-x64` 빌드 성공 확인
- [ ] 단일 EXE 파일 실행 확인
- [ ] WPF는 Native AOT 미지원이므로 ReadyToRun 방식 사용

---

## 5. 암호화 구현 가이드

### 5.1 키 교환 (ECDH)
```plaintext
1. 양측 모두 ECDiffieHellman 키 쌍 생성 (P-256)
2. 공개 키 교환 (TCP 연결 초기)
3. ECDiffieHellman.DeriveKeyMaterial()로 공유 비밀 생성
4. HKDF로 AES-256 키 + IV 유도
```

### 5.2 데이터 암호화
```plaintext
- 알고리즘: AES-256-GCM (AesGcm 클래스)
- 각 메시지/청크마다 고유 Nonce(12 bytes) 사용
- 프레임: [4B 길이][12B Nonce][16B Tag][N bytes 암호문]
- 파일 전송: 64KB 청크 단위로 개별 암호화
```

---

## 6. 네트워크 구현 가이드

### 6.1 UDP Discovery
- 포트: 52525 (상수 정의, 설정 가능)
- 초기 발송: 앱 시작 시 3회 빠르게 (100ms 간격)
- Heartbeat: 10초 간격
- 타임아웃: 30초 동안 Heartbeat 없으면 오프라인 처리
- Goodbye: 앱 정상 종료 시 발송

### 6.2 TCP Transfer
- 포트: 52526 (상수 정의, 설정 가능)
- 버퍼 크기: 64KB (파일 전송 청크)
- 소켓 옵션: `NoDelay = true`, `SendBufferSize = 1MB`, `ReceiveBufferSize = 1MB`
- 타임아웃: 연결 5초, 읽기/쓰기 30초 (대용량 파일은 청크 단위 갱신)

### 6.3 네트워크 대역 필터링
```plaintext
패턴: "192.168.100.*"
파싱: '*'를 0-255 범위로 확장하여 IP 매칭
예시:
  - "192.168.100.*" → 192.168.100.0 ~ 192.168.100.255
  - "10.0.*.*" → 10.0.0.0 ~ 10.0.255.255
```

---

## 7. UI 구현 가이드

### 7.1 드래그 앤 드롭
- `AllowDrop="True"` + `DragEnter`, `DragOver`, `Drop` 이벤트
- 드래그 진입 시 시각적 하이라이트 (배경색 변경, 아이콘 표시)
- `DataFormats.FileDrop`으로 파일 경로 추출
- 드롭 후 선택된 장치(들)에게 전송 시작

### 7.2 시스템 트레이
- `System.Windows.Forms.NotifyIcon` 사용 (WPF 내 WinForms 호스팅)
- 또는 Hardcodet.NotifyIcon.Wpf 패키지 사용
- 닫기 버튼 → `WindowState = Minimized` + `ShowInTaskbar = false`
- 트레이 더블클릭 → 창 복원

### 7.3 Toast 알림
- `Microsoft.Toolkit.Uwp.Notifications` 또는 직접 Windows API 호출
- Native AOT 호환성 확인 필수
- 알림 클릭 시 앱 활성화 + 수락/거부 UI 표시

---

## 8. 에러 처리 정책

### 8.1 네트워크 에러
- 연결 실패: 사용자에게 토스트로 알림, 재시도 불필요 (수동 재전송)
- 전송 중 끊김: 진행 중인 전송 정리, 임시 파일 삭제, 양측 알림
- 포트 충돌: 설정된 포트 사용 불가 시 사용자에게 알림

### 8.2 파일 에러
- 디스크 공간 부족: 수신 전 크기 확인, 부족 시 수신 거부
- 파일 접근 오류: 명확한 에러 메시지 표시
- 동일 파일명: 자동 번호 부여 (`file(1).txt`, `file(2).txt`)

### 8.3 로깅
- 디버그 빌드: 콘솔 + 파일 로깅
- 릴리스 빌드: 에러 수준만 파일 로깅
- 로그 위치: `%LOCALAPPDATA%/SimplyShare/logs/`

---

## 9. 의사결정 에스컬레이션

에이전트가 **임의로 판단하지 않고 반드시 사용자에게 물어야 하는** 사항:

1. spec.md에 명시되지 않은 새로운 기능 추가
2. 기존 설계 변경 (프로토콜, 포트, 암호화 방식 등)
3. 새로운 NuGet 패키지 도입
4. 프로젝트 구조 변경
5. UI/UX 흐름 변경
6. Native AOT 비호환으로 인한 대안 선택

---

## 10. 검증 체크리스트

각 Phase 완료 시 다음을 확인한다:

- [ ] `dotnet build` 성공 (경고 0개 목표)
- [ ] 해당 Phase의 핵심 기능 동작 확인
- [ ] Native AOT 트리밍 경고 없음
- [ ] Nullable 경고 없음
- [ ] 사용자에게 진행 상황 보고
