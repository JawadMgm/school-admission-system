import { Component, ReactNode, StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './styles.css';

// Last line of defence: show a message instead of a blank page if rendering ever throws.
class ErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() {
    return this.state.failed ? (
      <div className="app"><div className="banner error" role="alert">
        Something went wrong. <button onClick={() => location.reload()}>Reload page</button>
      </div></div>
    ) : this.props.children;
  }
}

createRoot(document.getElementById('root')!).render(
  <StrictMode><ErrorBoundary><App /></ErrorBoundary></StrictMode>,
);
