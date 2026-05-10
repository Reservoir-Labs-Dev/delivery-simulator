import React from 'react';
import './App.css';

function App() {
  return (
    <div className="app">
      <header className="header">
        <div className="header-inner">
          <div className="logo">
            <span className="logo-dot" />
            <span className="logo-text">Reservoir Labs</span>
          </div>
          <div className="header-tag">Order Processing Dashboard</div>
        </div>
      </header>

      <main className="main">
        <div className="hero">
          <div className="hero-meta">DOG-21 · dashboard scaffold</div>
          <h1 className="hero-title">
            Hello,<br />
            <span className="hero-accent">world.</span>
          </h1>
          <p className="hero-desc">
            React app is running. SignalR connection and order pipeline
            views will be wired in M1.
          </p>

          <div className="status-grid">
            <div className="status-item">
              <div className="status-label">React</div>
              <div className="status-value ok">✓ running</div>
            </div>
            <div className="status-item">
              <div className="status-label">Port</div>
              <div className="status-value">3000</div>
            </div>
            <div className="status-item">
              <div className="status-label">SignalR hub</div>
              <div className="status-value muted">not yet wired</div>
            </div>
            <div className="status-item">
              <div className="status-label">Order feed</div>
              <div className="status-value muted">not yet wired</div>
            </div>
          </div>
        </div>

        <div className="pipeline">
          <div className="pipeline-label">Pipeline services</div>
          <div className="pipeline-row">
            {[
              { name: 'OrderService',    port: '5294', color: 'blue'   },
              { name: 'PaymentService',  port: '5203', color: 'amber'  },
              { name: 'KitchenService',  port: '5233', color: 'teal'   },
              { name: 'DeliveryService', port: '5097', color: 'purple' },
            ].map((svc, i) => (
              <React.Fragment key={svc.name}>
                <div className={`pipeline-node pipeline-node--${svc.color}`}>
                  <div className="pipeline-node-name">{svc.name}</div>
                  <div className="pipeline-node-port">:{svc.port}</div>
                </div>
                {i < 3 && <div className="pipeline-arrow">→</div>}
              </React.Fragment>
            ))}
          </div>
        </div>
      </main>

      <footer className="footer">
        <span>Reservoir Labs · KIU Bachelor Thesis 2026</span>
        <span>Defense July 2026</span>
      </footer>
    </div>
  );
}

export default App;
